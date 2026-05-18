using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Zh1Zh1.CSharpConsole.Lite
{
    // Lite-mode binary tagged-union → Expression decoder.
    //
    // Reflection token formats: see LiteWireWriter.cs (writer / reader must match
    // byte-for-byte). Member resolution uses BindingFlags.Public|NonPublic|Static|Instance
    // to cover the spike-validated case set; lookup ambiguities are rare in practice and
    // fall under the §3.1 v1 known-limits clause.
    //
    // Reader owns the runtime ParameterExpression / LabelTarget identity map; ids encoded
    // by the matching writer are looked up here. A reused id returns the cached instance,
    // a fresh id allocates a new ParameterExpression / LabelTarget. Order-independent.
    public sealed class LiteWireReader
    {
        private readonly SessionTypeRegistry m_TypeReg;
        private readonly IDictionary<string, object> m_Slots;
        private readonly Dictionary<int, ParameterExpression> m_Params = new();
        private readonly Dictionary<int, LabelTarget> m_Labels = new();
        private BinaryReader m_Br;

        public LiteWireReader(SessionTypeRegistry typeReg, IDictionary<string, object> slots)
        {
            m_TypeReg = typeReg ?? throw new ArgumentNullException(nameof(typeReg));
            m_Slots = slots;
        }

        public Expression ReadRoot(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            using var ms = new MemoryStream(data, writable: false);
            using (m_Br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true))
            {
                var root = ReadNode();
                if (ms.Position != ms.Length)
                    throw new LiteWireException(
                        "E_LITE_WIRE_TRAILING_BYTES",
                        $"binary body has {ms.Length - ms.Position} trailing bytes after root expression");
                return root;
            }
        }

        // ============================================================ dispatch

        private Expression ReadNode()
        {
            var kind = (NodeKind)m_Br.ReadByte();
            switch (kind)
            {
                case NodeKind.Constant:       return ReadConstant();
                case NodeKind.Parameter:      return ReadParameter();
                case NodeKind.SlotsRef:       return ReadSlotsRef();
                case NodeKind.Lambda:         return ReadLambda();
                case NodeKind.Invoke:         return ReadInvoke();
                case NodeKind.Call:           return ReadCall();
                case NodeKind.MemberAccess:   return ReadMember();
                case NodeKind.Block:          return ReadBlock();
                case NodeKind.Conditional:    return ReadConditional();
                case NodeKind.Default:        return ReadDefault();
                case NodeKind.New:            return ReadNew();
                case NodeKind.NewArrayInit:   return ReadNewArray(isInit: true);
                case NodeKind.NewArrayBounds: return ReadNewArray(isInit: false);
                case NodeKind.TypeIs:         return ReadTypeBinary(isTypeIs: true);
                case NodeKind.TypeEqual:      return ReadTypeBinary(isTypeIs: false);
                case NodeKind.Loop:           return ReadLoop();
                case NodeKind.Goto:           return ReadGoto();
                case NodeKind.Label:          return ReadLabel();
                case NodeKind.Try:            return ReadTry();
                case NodeKind.Switch:         return ReadSwitch();
                case NodeKind.Index:          return ReadIndex();
                case NodeKind.MemberInit:     return ReadMemberInit();
                case NodeKind.ListInit:       return ReadListInit();
                case NodeKind.Unary:          return ReadUnary();
                case NodeKind.Binary:         return ReadBinary();
                default:
                    throw new LiteWireException(
                        "E_LITE_WIRE_BAD_NODEKIND",
                        $"unknown NodeKind byte 0x{(byte)kind:X2}");
            }
        }

        // ============================================================ leaves

        private Expression ReadConstant()
        {
            var t = m_TypeReg.Resolve(ReadVarInt());
            var v = ReadValue(t);
            return Expression.Constant(v, t);
        }

        private object ReadValue(Type staticType)
        {
            var vk = (ValueKind)m_Br.ReadByte();
            switch (vk)
            {
                case ValueKind.Null:    return null;
                case ValueKind.Bool:    return m_Br.ReadBoolean();
                case ValueKind.I8:      return m_Br.ReadSByte();
                case ValueKind.U8:      return m_Br.ReadByte();
                case ValueKind.I16:     return m_Br.ReadInt16();
                case ValueKind.U16:     return m_Br.ReadUInt16();
                case ValueKind.I32:     return ReadVarInt();
                case ValueKind.U32:     return m_Br.ReadUInt32();
                case ValueKind.I64:     return ReadVarInt64();
                case ValueKind.U64:     return m_Br.ReadUInt64();
                case ValueKind.F32:     return m_Br.ReadSingle();
                case ValueKind.F64:     return m_Br.ReadDouble();
                case ValueKind.Decimal:
                    return new decimal(new[] { m_Br.ReadInt32(), m_Br.ReadInt32(), m_Br.ReadInt32(), m_Br.ReadInt32() });
                case ValueKind.Char:    return (char)ReadVarInt();
                case ValueKind.Str:     return ReadString();
                case ValueKind.Type:    return m_TypeReg.Resolve(ReadVarInt());
                case ValueKind.Enum:
                {
                    var enumType = m_TypeReg.Resolve(ReadVarInt());
                    long raw = ReadVarInt64();
                    var underlying = Enum.GetUnderlyingType(enumType);
                    // ulong-backed enums round-trip via unchecked bit reinterpretation
                    // (writer encoded the bits with the same cast). Other underlying
                    // types use Convert.ChangeType which handles signed widening.
                    object boxed = underlying == typeof(ulong)
                        ? (object)unchecked((ulong)raw)
                        : Convert.ChangeType(raw, underlying, System.Globalization.CultureInfo.InvariantCulture);
                    return Enum.ToObject(enumType, boxed);
                }
                default:
                    throw new LiteWireException(
                        "E_LITE_WIRE_BAD_VALUEKIND",
                        $"unknown ValueKind byte 0x{(byte)vk:X2}");
            }
        }

        private ParameterExpression ReadParameter() => ReadParamData();

        private Expression ReadSlotsRef()
        {
            var t = m_TypeReg.Resolve(ReadVarInt());
            if (m_Slots == null)
                throw new LiteWireException(
                    "E_LITE_WIRE_SLOTS_UNBOUND",
                    "binary body contains a SlotsRef but reader was constructed without a slots dictionary");
            return Expression.Constant(m_Slots, t);
        }

        private Expression ReadDefault()
        {
            var t = m_TypeReg.Resolve(ReadVarInt());
            return Expression.Default(t);
        }

        // ============================================================ composites

        private Expression ReadLambda()
        {
            var delegateType = m_TypeReg.Resolve(ReadVarInt());
            int n = ReadVarInt();
            var ps = new ParameterExpression[n];
            for (int i = 0; i < n; i++) ps[i] = ReadParamData();
            var body = ReadNode();
            return Expression.Lambda(delegateType, body, ps);
        }

        private Expression ReadInvoke()
        {
            var callee = ReadNode();
            int n = ReadVarInt();
            var args = new Expression[n];
            for (int i = 0; i < n; i++) args[i] = ReadNode();
            return Expression.Invoke(callee, args);
        }

        private Expression ReadCall()
        {
            var method = ReadMethod();
            bool hasInstance = m_Br.ReadBoolean();
            Expression instance = hasInstance ? ReadNode() : null;
            int n = ReadVarInt();
            var args = new Expression[n];
            for (int i = 0; i < n; i++) args[i] = ReadNode();
            return Expression.Call(instance, method, args);
        }

        private Expression ReadMember()
        {
            byte memberKind = m_Br.ReadByte();
            MemberInfo member;
            switch (memberKind)
            {
                case 0: member = ReadProperty(); break;
                case 1: member = ReadField(); break;
                default:
                    throw new LiteWireException(
                        "E_LITE_WIRE_BAD_MEMBERKIND",
                        $"unknown member kind 0x{memberKind:X2} in MemberAccess");
            }
            bool hasInstance = m_Br.ReadBoolean();
            Expression instance = hasInstance ? ReadNode() : null;
            return Expression.MakeMemberAccess(instance, member);
        }

        private Expression ReadBlock()
        {
            var blockType = m_TypeReg.Resolve(ReadVarInt());
            int vn = ReadVarInt();
            var vars = new ParameterExpression[vn];
            for (int i = 0; i < vn; i++) vars[i] = ReadParamData();
            int sn = ReadVarInt();
            var stmts = new Expression[sn];
            for (int i = 0; i < sn; i++) stmts[i] = ReadNode();
            return Expression.Block(blockType, vars, stmts);
        }

        private Expression ReadConditional()
        {
            var t = m_TypeReg.Resolve(ReadVarInt());
            var test = ReadNode();
            var ifTrue = ReadNode();
            var ifFalse = ReadNode();
            return Expression.Condition(test, ifTrue, ifFalse, t);
        }

        private Expression ReadNew()
        {
            bool valueTypeDefault = m_Br.ReadBoolean();
            if (valueTypeDefault)
            {
                var t = m_TypeReg.Resolve(ReadVarInt());
                return Expression.New(t);
            }
            var ctor = ReadCtor();
            int n = ReadVarInt();
            var args = new Expression[n];
            for (int i = 0; i < n; i++) args[i] = ReadNode();
            return Expression.New(ctor, args);
        }

        private Expression ReadNewArray(bool isInit)
        {
            var elementType = m_TypeReg.Resolve(ReadVarInt());
            int n = ReadVarInt();
            var args = new Expression[n];
            for (int i = 0; i < n; i++) args[i] = ReadNode();
            return isInit
                ? Expression.NewArrayInit(elementType, args)
                : Expression.NewArrayBounds(elementType, args);
        }

        private Expression ReadTypeBinary(bool isTypeIs)
        {
            var typeOperand = m_TypeReg.Resolve(ReadVarInt());
            var operand = ReadNode();
            return isTypeIs
                ? Expression.TypeIs(operand, typeOperand)
                : Expression.TypeEqual(operand, typeOperand);
        }

        private Expression ReadLoop()
        {
            var brk = ReadOptionalLabel();
            var cont = ReadOptionalLabel();
            var body = ReadNode();
            if (brk == null && cont == null) return Expression.Loop(body);
            if (cont == null) return Expression.Loop(body, brk);
            return Expression.Loop(body, brk, cont);
        }

        private Expression ReadGoto()
        {
            var kind = (GotoExpressionKind)m_Br.ReadByte();
            var target = ReadLabelTarget();
            var t = m_TypeReg.Resolve(ReadVarInt());
            bool hasValue = m_Br.ReadBoolean();
            Expression val = hasValue ? ReadNode() : null;
            return Expression.MakeGoto(kind, target, val, t);
        }

        private Expression ReadLabel()
        {
            var target = ReadLabelTarget();
            bool hasDefault = m_Br.ReadBoolean();
            Expression dv = hasDefault ? ReadNode() : null;
            return Expression.Label(target, dv);
        }

        private Expression ReadTry()
        {
            var t = m_TypeReg.Resolve(ReadVarInt());
            var body = ReadNode();
            int n = ReadVarInt();
            var handlers = new CatchBlock[n];
            for (int i = 0; i < n; i++)
            {
                var test = m_TypeReg.Resolve(ReadVarInt());
                bool hasVar = m_Br.ReadBoolean();
                ParameterExpression variable = hasVar ? ReadParamData() : null;
                bool hasFilter = m_Br.ReadBoolean();
                Expression filter = hasFilter ? ReadNode() : null;
                var hbody = ReadNode();
                handlers[i] = Expression.MakeCatchBlock(test, variable, hbody, filter);
            }
            bool hasFinally = m_Br.ReadBoolean();
            Expression finallyExpr = hasFinally ? ReadNode() : null;
            bool hasFault = m_Br.ReadBoolean();
            Expression faultExpr = hasFault ? ReadNode() : null;
            return Expression.MakeTry(t, body, finallyExpr, faultExpr, handlers);
        }

        private Expression ReadSwitch()
        {
            var t = m_TypeReg.Resolve(ReadVarInt());
            var value = ReadNode();
            bool hasComparison = m_Br.ReadBoolean();
            MethodInfo comparison = hasComparison ? ReadMethod() : null;
            int n = ReadVarInt();
            var cases = new SwitchCase[n];
            for (int i = 0; i < n; i++)
            {
                int tn = ReadVarInt();
                var tests = new Expression[tn];
                for (int j = 0; j < tn; j++) tests[j] = ReadNode();
                var body = ReadNode();
                cases[i] = Expression.SwitchCase(body, tests);
            }
            bool hasDefault = m_Br.ReadBoolean();
            Expression defaultBody = hasDefault ? ReadNode() : null;
            return Expression.Switch(t, value, defaultBody, comparison, cases);
        }

        private Expression ReadIndex()
        {
            bool hasIndexer = m_Br.ReadBoolean();
            PropertyInfo indexer = hasIndexer ? ReadProperty() : null;
            var obj = ReadNode();
            int n = ReadVarInt();
            var args = new Expression[n];
            for (int i = 0; i < n; i++) args[i] = ReadNode();
            return indexer != null
                ? Expression.MakeIndex(obj, indexer, args)
                : Expression.ArrayAccess(obj, args);
        }

        private Expression ReadMemberInit()
        {
            var newExpr = (NewExpression)ReadNode();
            int n = ReadVarInt();
            var bindings = new MemberBinding[n];
            for (int i = 0; i < n; i++)
            {
                byte mk = m_Br.ReadByte();
                MemberInfo member;
                switch (mk)
                {
                    case 0: member = ReadProperty(); break;
                    case 1: member = ReadField(); break;
                    default:
                        throw new LiteWireException(
                            "E_LITE_WIRE_BAD_MEMBERKIND",
                            $"unknown member kind 0x{mk:X2} in MemberInit binding");
                }
                var expr = ReadNode();
                bindings[i] = Expression.Bind(member, expr);
            }
            return Expression.MemberInit(newExpr, bindings);
        }

        private Expression ReadListInit()
        {
            var newExpr = (NewExpression)ReadNode();
            int n = ReadVarInt();
            var inits = new ElementInit[n];
            for (int i = 0; i < n; i++)
            {
                var addMethod = ReadMethod();
                int an = ReadVarInt();
                var args = new Expression[an];
                for (int j = 0; j < an; j++) args[j] = ReadNode();
                inits[i] = Expression.ElementInit(addMethod, args);
            }
            return Expression.ListInit(newExpr, inits);
        }

        // ============================================================ unary / binary

        private Expression ReadUnary()
        {
            var op = (UnaryOp)m_Br.ReadByte();
            var t = m_TypeReg.Resolve(ReadVarInt());
            var operand = ReadNode();
            bool hasMethod = m_Br.ReadBoolean();
            MethodInfo method = hasMethod ? ReadMethod() : null;
            return Expression.MakeUnary(LiteWireWriter.UnaryOpToExpressionType(op), operand, t, method);
        }

        private Expression ReadBinary()
        {
            var op = (BinaryOp)m_Br.ReadByte();
            var left = ReadNode();
            var right = ReadNode();
            bool liftToNull = m_Br.ReadBoolean();
            bool hasMethod = m_Br.ReadBoolean();
            MethodInfo method = hasMethod ? ReadMethod() : null;
            bool hasConversion = m_Br.ReadBoolean();
            LambdaExpression conversion = hasConversion ? (LambdaExpression)ReadNode() : null;
            return Expression.MakeBinary(LiteWireWriter.BinaryOpToExpressionType(op), left, right, liftToNull, method, conversion);
        }

        // ============================================================ reflection tokens

        private MethodInfo ReadMethod()
        {
            var declType = m_TypeReg.Resolve(ReadVarInt());
            var name = ReadString();
            bool isStatic = m_Br.ReadBoolean();
            int pn = ReadVarInt();
            var paramTypes = new Type[pn];
            for (int i = 0; i < pn; i++) paramTypes[i] = m_TypeReg.Resolve(ReadVarInt());
            int gn = ReadVarInt();
            Type[] typeArgs = gn > 0 ? new Type[gn] : null;
            for (int i = 0; i < gn; i++) typeArgs[i] = m_TypeReg.Resolve(ReadVarInt());

            var flags = BindingFlags.Public | BindingFlags.NonPublic
                | (isStatic ? BindingFlags.Static : BindingFlags.Instance);

            if (gn == 0)
            {
                // Non-generic path: BCL's exact-signature match naturally excludes
                // generic method definitions (open T parameter cannot equal concrete
                // typeArg). Matches spike at PhaseBSpike.cs:1659.
                var info = declType.GetMethod(name, flags, binder: null, types: paramTypes, modifiers: null);
                if (info == null)
                    throw new LiteWireException(
                        "E_LITE_WIRE_METHOD_NOT_FOUND",
                        $"method '{name}' not found on {declType} (isStatic={isStatic}, params={pn})");
                return info;
            }

            // Generic path: iterate, require IsGenericMethodDefinition + matching arity,
            // catch MakeGenericMethod failures (constraint violation on irrelevant overloads).
            foreach (var m in declType.GetMethods(flags))
            {
                if (m.Name != name) continue;
                if (m.IsStatic != isStatic) continue;
                if (!m.IsGenericMethodDefinition) continue;
                if (m.GetGenericArguments().Length != gn) continue;
                if (m.GetParameters().Length != pn) continue;

                MethodInfo closed;
                try { closed = m.MakeGenericMethod(typeArgs); }
                catch (ArgumentException) { continue; }

                var cps = closed.GetParameters();
                bool match = true;
                for (int i = 0; i < pn; i++)
                {
                    if (cps[i].ParameterType != paramTypes[i]) { match = false; break; }
                }
                if (match) return closed;
            }
            throw new LiteWireException(
                "E_LITE_WIRE_METHOD_NOT_FOUND",
                $"generic method '{name}' not found on {declType} (isStatic={isStatic}, params={pn}, generic args={gn})");
        }

        private ConstructorInfo ReadCtor()
        {
            var declType = m_TypeReg.Resolve(ReadVarInt());
            int n = ReadVarInt();
            var paramTypes = new Type[n];
            for (int i = 0; i < n; i++) paramTypes[i] = m_TypeReg.Resolve(ReadVarInt());

            var ctor = declType.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: paramTypes, modifiers: null);
            if (ctor == null)
                throw new LiteWireException(
                    "E_LITE_WIRE_CTOR_NOT_FOUND",
                    $"constructor on {declType} with {n} params not found");
            return ctor;
        }

        private PropertyInfo ReadProperty()
        {
            var declType = m_TypeReg.Resolve(ReadVarInt());
            var name = ReadString();
            int n = ReadVarInt();
            var idxTypes = new Type[n];
            for (int i = 0; i < n; i++) idxTypes[i] = m_TypeReg.Resolve(ReadVarInt());

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static;

            PropertyInfo pi = n == 0
                ? declType.GetProperty(name, flags)
                : declType.GetProperty(name, flags, binder: null, returnType: null, types: idxTypes, modifiers: null);
            if (pi == null)
                throw new LiteWireException(
                    "E_LITE_WIRE_PROPERTY_NOT_FOUND",
                    $"property '{name}' on {declType} (indexParams={n}) not found");
            return pi;
        }

        private FieldInfo ReadField()
        {
            var declType = m_TypeReg.Resolve(ReadVarInt());
            var name = ReadString();
            var fi = declType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static);
            if (fi == null)
                throw new LiteWireException(
                    "E_LITE_WIRE_FIELD_NOT_FOUND",
                    $"field '{name}' on {declType} not found");
            return fi;
        }

        // ============================================================ identity tables

        private ParameterExpression ReadParamData()
        {
            int id = ReadVarInt();
            var type = m_TypeReg.Resolve(ReadVarInt());
            var name = ReadString();
            if (m_Params.TryGetValue(id, out var existing)) return existing;
            var p = Expression.Parameter(type, name);
            m_Params[id] = p;
            return p;
        }

        private LabelTarget ReadOptionalLabel()
        {
            bool has = m_Br.ReadBoolean();
            return has ? ReadLabelTarget() : null;
        }

        private LabelTarget ReadLabelTarget()
        {
            int id = ReadVarInt();
            var type = m_TypeReg.Resolve(ReadVarInt());
            var name = ReadString();
            if (m_Labels.TryGetValue(id, out var existing)) return existing;
            var t = Expression.Label(type, name);
            m_Labels[id] = t;
            return t;
        }

        // ============================================================ primitives

        private string ReadString()
        {
            int len = ReadVarInt();
            if (len == -1) return null;
            if (len < 0)
                throw new LiteWireException(
                    "E_LITE_WIRE_BAD_STRING_LEN",
                    $"invalid string length {len} (only -1 sentinel or non-negative byte counts allowed)");
            if (len == 0) return string.Empty;
            var bytes = m_Br.ReadBytes(len);
            if (bytes.Length != len)
                throw new LiteWireException(
                    "E_LITE_WIRE_STRING_TRUNCATED",
                    $"expected {len} string bytes, got {bytes.Length} (stream truncated)");
            return Encoding.UTF8.GetString(bytes);
        }

        internal int ReadVarInt()
        {
            int result = 0;
            int shift = 0;
            while (true)
            {
                byte b = m_Br.ReadByte();
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift >= 35)
                    throw new LiteWireException(
                        "E_LITE_WIRE_VARINT_OVERFLOW",
                        "varint exceeds 5 bytes (int32 max width)");
            }
        }

        internal long ReadVarInt64()
        {
            long result = 0;
            int shift = 0;
            while (true)
            {
                byte b = m_Br.ReadByte();
                result |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift >= 70)
                    throw new LiteWireException(
                        "E_LITE_WIRE_VARINT_OVERFLOW",
                        "varint64 exceeds 10 bytes (int64 max width)");
            }
        }
    }
}
