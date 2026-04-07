using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Zh1Zh1.CSharpConsole.Service;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Zh1Zh1.CSharpConsole.Service.Internal
{
    internal sealed class MainThreadRequestRunner
    {
        private const string DISPATCHER_NOT_INITIALIZED_MESSAGE = "Main-thread dispatcher is not initialized. Ensure MainThreadRequestRunner.InitializeEditor() or InitializeRuntime() is called during startup.";

        private readonly static Queue<Action> s_SharedQueue = new Queue<Action>();
        private readonly static object s_SharedQueueLock = new object();

        private readonly static Queue<Action> s_RuntimePendingActions = new Queue<Action>();
        private readonly static object s_RuntimeLock = new object();
        private static MainThreadRequestRunnerDriver s_RuntimeDriver;

#if UNITY_EDITOR
        private readonly static Queue<Action> s_EditorPendingActions = new Queue<Action>();
        private readonly static object s_EditorLock = new object();
        private static bool s_EditorRegistered;
#endif

        private static Action<Action> s_PlatformPostToMainThread;
        private static int s_DrainScheduled;

        private readonly static SemaphoreSlim s_AsyncRunLock = new SemaphoreSlim(1, 1);

        [ThreadStatic]
        private static bool s_IsExecutingOnMainThread;

        private static bool IsOnMainThread()
        {
            return s_IsExecutingOnMainThread;
        }

        public static void InitializeRuntime()
        {
            lock (s_RuntimeLock)
            {
                if (s_RuntimeDriver == null)
                {
                    var go = new GameObject("[CSharpConsole] MainThreadRequestRunner");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    s_RuntimeDriver = go.AddComponent<MainThreadRequestRunnerDriver>();
                }
            }

            SetPlatformPostToMainThread(RuntimePost);
        }

        public static void InitializeEditor()
        {
#if UNITY_EDITOR
            SetPlatformPostToMainThread(EditorPost);
#else
            throw new InvalidOperationException("InitializeEditor can only be called in the Unity Editor.");
#endif
        }

        public static void Post(Action work)
        {
            if (work == null)
            {
                return;
            }

            Action<Action> postToMainThread;
            lock (s_SharedQueueLock)
            {
                postToMainThread = GetPlatformPostToMainThreadOrThrow();
                s_SharedQueue.Enqueue(work);
                if (s_DrainScheduled != 0)
                {
                    return;
                }

                s_DrainScheduled = 1;
            }

            postToMainThread(DrainSharedQueue);
        }

        public static T RunOnMainThread<T>(Func<T> work)
        {
            return RunOnMainThread(work, ConsoleServiceConfig.MainThreadTimeoutMs);
        }

        public static T RunOnMainThread<T>(Func<T> work, int timeoutMs)
        {
            if (work == null)
            {
                return default;
            }

            // If already on the main thread, execute synchronously to avoid deadlock.
            if (IsOnMainThread())
            {
                return work();
            }

            var postToMainThread = GetPlatformPostToMainThreadOrThrow();

            T result = default;
            Exception exception = null;
            using var done = new ManualResetEventSlim(false);

            postToMainThread(() =>
            {
                try
                {
                    result = work();
                }
                catch (Exception e)
                {
                    exception = e;
                }
                finally
                {
                    done.Set();
                }
            });

            if (!done.Wait(timeoutMs))
            {
                throw new TimeoutException("Timeout: main thread execution timed out");
            }

            if (exception != null)
            {
                throw exception;
            }

            return result;
        }

        public static Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> work)
        {
            return RunOnMainThreadAsync(work, ConsoleServiceConfig.MainThreadTimeoutMs);
        }

        public static Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> work, int timeoutMs)
        {
            if (work == null)
            {
                return Task.FromResult(default(T));
            }

            // If already on the main thread, execute synchronously to avoid deadlock.
            if (IsOnMainThread())
            {
                return work();
            }

            return RunOnMainThreadAsyncCore(work, timeoutMs);
        }

        private static async Task<T> RunOnMainThreadAsyncCore<T>(Func<Task<T>> work, int timeoutMs)
        {
            await s_AsyncRunLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var postToMainThread = GetPlatformPostToMainThreadOrThrow();
                var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

                postToMainThread(() =>
                {
                    var previousContext = SynchronizationContext.Current;
                    var bridgeContext = new MainThreadRequestRunnerSynchronizationContext(postToMainThread);
                    SynchronizationContext.SetSynchronizationContext(bridgeContext);

                    Task<T> task;
                    try
                    {
                        task = work();
                    }
                    catch (Exception e)
                    {
                        tcs.TrySetException(e);
                        return;
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(previousContext);
                    }

                    if (task == null)
                    {
                        tcs.TrySetResult(default);
                        return;
                    }

                    _ = CompleteAsyncWork(task, tcs);
                });

                return await AwaitWithTimeoutAsync(tcs.Task, timeoutMs).ConfigureAwait(false);
            }
            finally
            {
                s_AsyncRunLock.Release();
            }
        }

        private static Action<Action> GetPlatformPostToMainThreadOrThrow()
        {
            var postToMainThread = s_PlatformPostToMainThread;
            if (postToMainThread != null)
            {
                return postToMainThread;
            }

            Debug.Assert(false, DISPATCHER_NOT_INITIALIZED_MESSAGE);
            throw new InvalidOperationException(DISPATCHER_NOT_INITIALIZED_MESSAGE);
        }

        private static void SetPlatformPostToMainThread(Action<Action> postToMainThread)
        {
            var shouldRescheduleDrain = false;
            lock (s_SharedQueueLock)
            {
                s_PlatformPostToMainThread = postToMainThread;
                if (s_DrainScheduled != 0)
                {
                    if (s_SharedQueue.Count == 0)
                    {
                        s_DrainScheduled = 0;
                    }
                    else
                    {
                        shouldRescheduleDrain = true;
                    }
                }
            }

            if (shouldRescheduleDrain)
            {
                postToMainThread(DrainSharedQueue);
            }
        }

        private static void RuntimePost(Action action)
        {
            if (action == null)
            {
                return;
            }

            lock (s_RuntimeLock)
            {
                s_RuntimePendingActions.Enqueue(action);
            }
        }

        private static void DrainRuntimePendingActions()
        {
            while (true)
            {
                Action action;
                lock (s_RuntimeLock)
                {
                    if (s_RuntimePendingActions.Count == 0)
                    {
                        return;
                    }

                    action = s_RuntimePendingActions.Dequeue();
                }

                try
                {
                    s_IsExecutingOnMainThread = true;
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
                finally
                {
                    s_IsExecutingOnMainThread = false;
                }
            }
        }

#if UNITY_EDITOR
        private static void EditorPost(Action action)
        {
            if (action == null)
            {
                return;
            }

            lock (s_EditorLock)
            {
                s_EditorPendingActions.Enqueue(action);
                if (!s_EditorRegistered)
                {
                    s_EditorRegistered = true;
                    EditorApplication.update += ProcessEditorPendingActions;
                }
            }
        }

        private static void ProcessEditorPendingActions()
        {
            Action[] actionsToProcess;
            lock (s_EditorLock)
            {
                if (s_EditorPendingActions.Count == 0)
                {
                    s_EditorRegistered = false;
                    EditorApplication.update -= ProcessEditorPendingActions;
                    return;
                }

                actionsToProcess = s_EditorPendingActions.ToArray();
                s_EditorPendingActions.Clear();
            }

            foreach (var action in actionsToProcess)
            {
                try
                {
                    s_IsExecutingOnMainThread = true;
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
                finally
                {
                    s_IsExecutingOnMainThread = false;
                }
            }
        }
#endif

        private static void DrainSharedQueue()
        {
            while (true)
            {
                Action next;
                lock (s_SharedQueueLock)
                {
                    if (s_SharedQueue.Count == 0)
                    {
                        s_DrainScheduled = 0;
                        return;
                    }

                    next = s_SharedQueue.Dequeue();
                }

                try
                {
                    s_IsExecutingOnMainThread = true;
                    next();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
                finally
                {
                    s_IsExecutingOnMainThread = false;
                }
            }
        }

        private static async Task CompleteAsyncWork<T>(Task<T> task, TaskCompletionSource<T> tcs)
        {
            try
            {
                var result = await task;
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException e)
            {
                if (task.IsCanceled)
                {
                    tcs.TrySetCanceled();
                    return;
                }

                tcs.TrySetException(e);
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        }

        private static async Task<T> AwaitWithTimeoutAsync<T>(Task<T> task, int timeoutMs)
        {
            using var timeoutCts = new CancellationTokenSource();
            var timeoutTask = Task.Delay(timeoutMs, timeoutCts.Token);
            var completedTask = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
            if (completedTask != task)
            {
                throw new TimeoutException("Timeout: main thread execution timed out");
            }

            timeoutCts.Cancel();
            return await task.ConfigureAwait(false);
        }

        private sealed class MainThreadRequestRunnerSynchronizationContext : SynchronizationContext
        {
            private readonly Action<Action> _postToMainThread;

            public MainThreadRequestRunnerSynchronizationContext(Action<Action> postToMainThread)
            {
                _postToMainThread = postToMainThread ?? throw new ArgumentNullException(nameof(postToMainThread));
            }

            public override void Post(SendOrPostCallback d, object state)
            {
                if (d == null)
                {
                    return;
                }

                _postToMainThread(() =>
                {
                    var previousContext = Current;
                    SetSynchronizationContext(this);
                    try
                    {
                        d(state);
                    }
                    finally
                    {
                        SetSynchronizationContext(previousContext);
                    }
                });
            }
        }

        private sealed class MainThreadRequestRunnerDriver : MonoBehaviour
        {
            private void Update()
            {
                DrainRuntimePendingActions();
            }

            private void OnDestroy()
            {
                if (s_RuntimeDriver == this)
                {
                    s_RuntimeDriver = null;
                }
            }
        }
    }
}
