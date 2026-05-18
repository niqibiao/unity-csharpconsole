using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Zh1Zh1.CSharpConsole.Lite
{
    // Lite-mode Expression → binary tagged-union encoder.
    //
    // Reflection token formats (declTypeId is a varint into SessionTypeRegistry):
    //   MethodInfo:      [declTypeId][methodName: str][isStatic: bool]
    //                    [argTypeCount: varint][argTypeId...]
    //                    [genericArgCount: varint][genericArgId...]
    //   ConstructorInfo: [declTypeId][argTypeCount: varint][argTypeId...]
    //   PropertyInfo:    [declTypeId][propName: str]
    //                    [indexParamCount: varint][indexParamTypeId...]
    //   FieldInfo:       [declTypeId][fieldName: str]
    //
    // Per §3.1 single-flight invariant, one instance per top-level serialization.
    public sealed class LiteWireWriter
    {
        private readonly SessionTypeRegistry m_TypeReg;
        private readonly IDictionary<string, object> m_Slots;
        private readonly Dictionary<ParameterExpression, int> m_ParamIds = new();
        private readonly Dictionary<LabelTarget, int> m_LabelIds = new();
        private BinaryWriter m_Bw;

        public LiteWireWriter(SessionTypeRegistry typeReg, IDictionary<string, object> slots)
        {
            m_TypeReg = typeReg ?? throw new ArgumentNullException(nameof(typeReg));
            m_Slots = slots;
        }

        public byte[] WriteRoot(Expression e)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            using var ms = new MemoryStream();
            using (m_Bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                WriteNode(e);
            }
            m_Bw = null;
            return ms.ToArray();
        }

        // ============================================================ dispatch

        private void WriteNode(Expression e)
        {
            if (e == null)
                throw new LiteWireException("E_LITE_WIRE_NULL_NODE", "encountered null child Expression during serialization");

            switch (e.NodeType)
            {
                case ExpressionType.Constant:        WriteConstant((ConstantExpression)e); return;
                case ExpressionType.Parameter:       WriteParameter((ParameterExpression)e); return;
                case ExpressionType.Lambda:          WriteLambda((LambdaExpression)e); return;
                case ExpressionType.Invoke:          WriteInvoke((InvocationExpression)e); return;
                case ExpressionType.Call:            WriteCall((MethodCallExpression)e); return;
                case ExpressionType.MemberAccess:    WriteMember((MemberExpression)e); return;
                case ExpressionType.Block:           WriteBlock((BlockExpression)e); return;
                case ExpressionType.Conditional:     WriteConditional((ConditionalExpression)e); return;
                case ExpressionType.New:             WriteNew((NewExpression)e); return;
                case ExpressionType.NewArrayInit:
                case ExpressionType.NewArrayBounds:  WriteNewArray((NewArrayExpression)e); return;
                case ExpressionType.TypeIs:
                case ExpressionType.TypeEqual:       WriteTypeBinary((TypeBinaryExpression)e); return;
                case ExpressionType.Default:         WriteDefault((DefaultExpression)e); return;
                case ExpressionType.Loop:            WriteLoop((LoopExpression)e); return;
                case ExpressionType.Goto:            WriteGoto((GotoExpression)e); return;
                case ExpressionType.Label:           WriteLabel((LabelExpression)e); return;
                case ExpressionType.Try:             WriteTry((TryExpression)e); return;
                case ExpressionType.Switch:          WriteSwitch((SwitchExpression)e); return;
                case ExpressionType.Index:           WriteIndex((IndexExpression)e); return;
                case ExpressionType.MemberInit:      WriteMemberInit((MemberInitExpression)e); return;
                case ExpressionType.ListInit:        WriteListInit((ListInitExpression)e); return;
            }

            if (IsUnary(e.NodeType)) { WriteUnary((UnaryExpression)e); return; }
            if (IsBinary(e.NodeType)) { WriteBinary((BinaryExpression)e); return; }

            throw new LiteWireException(
                "E_LITE_UNSUPPORTED_NODE",
                $"unsupported ExpressionType {e.NodeType} during serialization");
        }

        // ============================================================ leaves

        private void WriteConstant(ConstantExpression c)
        {
            if (m_Slots != null && c.Value is IDictionary<string, object> dict && ReferenceEquals(dict, m_Slots))
            {
                m_Bw.Write((byte)NodeKind.SlotsRef);
                WriteVarInt(m_TypeReg.GetOrRegister(c.Type));
                return;
            }

            if (c.Value is Dictionary<string, object>)
            {
                throw new LiteWireException(
                    "E_LITE_CONSTANT_NONSCALAR",
                    "ConstantExpression carries a Dictionary<string,object> that is not the session slots bag; reject to prevent silent fallback to generic object serialization");
            }

            m_Bw.Write((byte)NodeKind.Constant);
            WriteVarInt(m_TypeReg.GetOrRegister(c.Type));
            WriteValue(c.Value, c.Type);
        }

        private void WriteValue(object v, Type staticType)
        {
            if (v == null)
            {
                m_Bw.Write((byte)ValueKind.Null);
                return;
            }

            var rt = v.GetType();

            if (rt.IsEnum)
            {
                m_Bw.Write((byte)ValueKind.Enum);
                WriteVarInt(m_TypeReg.GetOrRegister(rt));
                // Preserve bit pattern across varint64 regardless of underlying signedness.
                // ulong-backed enums can hold values > long.MaxValue; Convert.ToInt64
                // would throw OverflowException for those, so route ulong via unchecked
                // bit reinterpretation. Reader uses the enum's underlying type to
                // reconstruct, so wire format stays signedness-agnostic.
                long bits = Enum.GetUnderlyingType(rt) == typeof(ulong)
                    ? unchecked((long)Convert.ToUInt64(v, System.Globalization.CultureInfo.InvariantCulture))
                    : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
                WriteVarInt64(bits);
                return;
            }

            switch (Type.GetTypeCode(rt))
            {
                case TypeCode.Boolean: m_Bw.Write((byte)ValueKind.Bool); m_Bw.Write((bool)v); return;
                case TypeCode.SByte:   m_Bw.Write((byte)ValueKind.I8);   m_Bw.Write((sbyte)v); return;
                case TypeCode.Byte:    m_Bw.Write((byte)ValueKind.U8);   m_Bw.Write((byte)v); return;
                case TypeCode.Int16:   m_Bw.Write((byte)ValueKind.I16);  m_Bw.Write((short)v); return;
                case TypeCode.UInt16:  m_Bw.Write((byte)ValueKind.U16);  m_Bw.Write((ushort)v); return;
                case TypeCode.Int32:   m_Bw.Write((byte)ValueKind.I32);  WriteVarInt((int)v); return;
                case TypeCode.UInt32:  m_Bw.Write((byte)ValueKind.U32);  m_Bw.Write((uint)v); return;
                case TypeCode.Int64:   m_Bw.Write((byte)ValueKind.I64);  WriteVarInt64((long)v); return;
                case TypeCode.UInt64:  m_Bw.Write((byte)ValueKind.U64);  m_Bw.Write((ulong)v); return;
                case TypeCode.Single:  m_Bw.Write((byte)ValueKind.F32);  m_Bw.Write((float)v); return;
                case TypeCode.Double:  m_Bw.Write((byte)ValueKind.F64);  m_Bw.Write((double)v); return;
                case TypeCode.Decimal:
                {
                    m_Bw.Write((byte)ValueKind.Decimal);
                    var bits = decimal.GetBits((decimal)v);
                    m_Bw.Write(bits[0]); m_Bw.Write(bits[1]); m_Bw.Write(bits[2]); m_Bw.Write(bits[3]);
                    return;
                }
                case TypeCode.Char:    m_Bw.Write((byte)ValueKind.Char); WriteVarInt((char)v); return;
                case TypeCode.String:  m_Bw.Write((byte)ValueKind.Str);  WriteString((string)v); return;
            }

            if (v is Type t)
            {
                m_Bw.Write((byte)ValueKind.Type);
                WriteVarInt(m_TypeReg.GetOrRegister(t));
                return;
            }

            throw new LiteWireException(
                "E_LITE_CONSTANT_NONSCALAR",
                $"cannot serialize ConstantExpression of runtime type '{rt.FullName}'; only primitives, decimal, char, string, System.Type, enums, and the session slots dictionary are supported as Constant payload");
        }

        private void WriteParameter(ParameterExpression p)
        {
            m_Bw.Write((byte)NodeKind.Parameter);
            WriteVarInt(IdForParam(p));
            WriteVarInt(m_TypeReg.GetOrRegister(p.Type));
            WriteString(p.Name);
        }

        private void WriteDefault(DefaultExpression d)
        {
            m_Bw.Write((byte)NodeKind.Default);
            WriteVarInt(m_TypeReg.GetOrRegister(d.Type));
        }

        // ============================================================ composites

        private void WriteLambda(LambdaExpression l)
        {
            m_Bw.Write((byte)NodeKind.Lambda);
            WriteVarInt(m_TypeReg.GetOrRegister(l.Type));
            WriteVarInt(l.Parameters.Count);
            foreach (var p in l.Parameters) WriteParameterDecl(p);
            WriteNode(l.Body);
        }

        // Used when a parameter is being declared in a lambda / block scope.
        // Distinct from WriteParameter (Parameter NodeKind = reference back to a declared param).
        private void WriteParameterDecl(ParameterExpression p)
        {
            WriteVarInt(IdForParam(p));
            WriteVarInt(m_TypeReg.GetOrRegister(p.Type));
            WriteString(p.Name);
        }

        private void WriteInvoke(InvocationExpression i)
        {
            m_Bw.Write((byte)NodeKind.Invoke);
            WriteNode(i.Expression);
            WriteVarInt(i.Arguments.Count);
            foreach (var a in i.Arguments) WriteNode(a);
        }

        private void WriteCall(MethodCallExpression c)
        {
            m_Bw.Write((byte)NodeKind.Call);
            WriteMethod(c.Method);
            m_Bw.Write(c.Object != null);
            if (c.Object != null) WriteNode(c.Object);
            WriteVarInt(c.Arguments.Count);
            foreach (var a in c.Arguments) WriteNode(a);
        }

        private void WriteMember(MemberExpression m)
        {
            m_Bw.Write((byte)NodeKind.MemberAccess);
            // memberKind: 0 = Property, 1 = Field
            switch (m.Member)
            {
                case PropertyInfo pi:
                    m_Bw.Write((byte)0);
                    WriteProperty(pi);
                    break;
                case FieldInfo fi:
                    m_Bw.Write((byte)1);
                    WriteField(fi);
                    break;
                default:
                    throw new LiteWireException(
                        "E_LITE_UNSUPPORTED_MEMBER",
                        $"unsupported MemberAccess member type {m.Member.MemberType}");
            }
            m_Bw.Write(m.Expression != null);
            if (m.Expression != null) WriteNode(m.Expression);
        }

        private void WriteBlock(BlockExpression b)
        {
            m_Bw.Write((byte)NodeKind.Block);
            WriteVarInt(m_TypeReg.GetOrRegister(b.Type));
            WriteVarInt(b.Variables.Count);
            foreach (var v in b.Variables) WriteParameterDecl(v);
            WriteVarInt(b.Expressions.Count);
            foreach (var s in b.Expressions) WriteNode(s);
        }

        private void WriteConditional(ConditionalExpression c)
        {
            m_Bw.Write((byte)NodeKind.Conditional);
            WriteVarInt(m_TypeReg.GetOrRegister(c.Type));
            WriteNode(c.Test);
            WriteNode(c.IfTrue);
            WriteNode(c.IfFalse);
        }

        private void WriteNew(NewExpression n)
        {
            m_Bw.Write((byte)NodeKind.New);
            // valueTypeDefault flag: ctor is null only for value-type default construction
            bool valueTypeDefault = n.Constructor == null;
            m_Bw.Write(valueTypeDefault);
            if (valueTypeDefault)
            {
                WriteVarInt(m_TypeReg.GetOrRegister(n.Type));
                return;
            }
            WriteCtor(n.Constructor);
            WriteVarInt(n.Arguments.Count);
            foreach (var a in n.Arguments) WriteNode(a);
        }

        private void WriteNewArray(NewArrayExpression e)
        {
            bool isInit = e.NodeType == ExpressionType.NewArrayInit;
            m_Bw.Write((byte)(isInit ? NodeKind.NewArrayInit : NodeKind.NewArrayBounds));
            WriteVarInt(m_TypeReg.GetOrRegister(e.Type.GetElementType()));
            WriteVarInt(e.Expressions.Count);
            foreach (var x in e.Expressions) WriteNode(x);
        }

        private void WriteTypeBinary(TypeBinaryExpression e)
        {
            bool isTypeIs = e.NodeType == ExpressionType.TypeIs;
            m_Bw.Write((byte)(isTypeIs ? NodeKind.TypeIs : NodeKind.TypeEqual));
            WriteVarInt(m_TypeReg.GetOrRegister(e.TypeOperand));
            WriteNode(e.Expression);
        }

        private void WriteLoop(LoopExpression e)
        {
            m_Bw.Write((byte)NodeKind.Loop);
            WriteOptionalLabel(e.BreakLabel);
            WriteOptionalLabel(e.ContinueLabel);
            WriteNode(e.Body);
        }

        private void WriteGoto(GotoExpression g)
        {
            m_Bw.Write((byte)NodeKind.Goto);
            m_Bw.Write((byte)g.Kind);
            WriteLabelTarget(g.Target);
            WriteVarInt(m_TypeReg.GetOrRegister(g.Type));
            m_Bw.Write(g.Value != null);
            if (g.Value != null) WriteNode(g.Value);
        }

        private void WriteLabel(LabelExpression l)
        {
            m_Bw.Write((byte)NodeKind.Label);
            WriteLabelTarget(l.Target);
            m_Bw.Write(l.DefaultValue != null);
            if (l.DefaultValue != null) WriteNode(l.DefaultValue);
        }

        private void WriteTry(TryExpression t)
        {
            m_Bw.Write((byte)NodeKind.Try);
            WriteVarInt(m_TypeReg.GetOrRegister(t.Type));
            WriteNode(t.Body);
            WriteVarInt(t.Handlers.Count);
            foreach (var h in t.Handlers)
            {
                WriteVarInt(m_TypeReg.GetOrRegister(h.Test));
                m_Bw.Write(h.Variable != null);
                if (h.Variable != null) WriteParameterDecl(h.Variable);
                m_Bw.Write(h.Filter != null);
                if (h.Filter != null) WriteNode(h.Filter);
                WriteNode(h.Body);
            }
            m_Bw.Write(t.Finally != null);
            if (t.Finally != null) WriteNode(t.Finally);
            m_Bw.Write(t.Fault != null);
            if (t.Fault != null) WriteNode(t.Fault);
        }

        private void WriteSwitch(SwitchExpression s)
        {
            m_Bw.Write((byte)NodeKind.Switch);
            WriteVarInt(m_TypeReg.GetOrRegister(s.Type));
            WriteNode(s.SwitchValue);
            m_Bw.Write(s.Comparison != null);
            if (s.Comparison != null) WriteMethod(s.Comparison);
            WriteVarInt(s.Cases.Count);
            foreach (var c in s.Cases)
            {
                WriteVarInt(c.TestValues.Count);
                foreach (var t in c.TestValues) WriteNode(t);
                WriteNode(c.Body);
            }
            m_Bw.Write(s.DefaultBody != null);
            if (s.DefaultBody != null) WriteNode(s.DefaultBody);
        }

        private void WriteIndex(IndexExpression e)
        {
            m_Bw.Write((byte)NodeKind.Index);
            m_Bw.Write(e.Indexer != null);
            if (e.Indexer != null) WriteProperty(e.Indexer);
            WriteNode(e.Object);
            WriteVarInt(e.Arguments.Count);
            foreach (var a in e.Arguments) WriteNode(a);
        }

        private void WriteMemberInit(MemberInitExpression m)
        {
            m_Bw.Write((byte)NodeKind.MemberInit);
            WriteNode(m.NewExpression);
            WriteVarInt(m.Bindings.Count);
            foreach (var b in m.Bindings)
            {
                if (b.BindingType != MemberBindingType.Assignment)
                    throw new LiteWireException(
                        "E_LITE_UNSUPPORTED_MEMBERBINDING",
                        $"MemberInit binding type {b.BindingType} not supported (only Assignment)");
                var ma = (MemberAssignment)b;
                switch (ma.Member)
                {
                    case PropertyInfo pi:
                        m_Bw.Write((byte)0);
                        WriteProperty(pi);
                        break;
                    case FieldInfo fi:
                        m_Bw.Write((byte)1);
                        WriteField(fi);
                        break;
                    default:
                        throw new LiteWireException(
                            "E_LITE_UNSUPPORTED_MEMBER",
                            $"MemberInit Assignment member type {ma.Member.MemberType} not supported");
                }
                WriteNode(ma.Expression);
            }
        }

        private void WriteListInit(ListInitExpression l)
        {
            m_Bw.Write((byte)NodeKind.ListInit);
            WriteNode(l.NewExpression);
            WriteVarInt(l.Initializers.Count);
            foreach (var init in l.Initializers)
            {
                WriteMethod(init.AddMethod);
                WriteVarInt(init.Arguments.Count);
                foreach (var a in init.Arguments) WriteNode(a);
            }
        }

        // ============================================================ unary / binary

        private void WriteUnary(UnaryExpression u)
        {
            m_Bw.Write((byte)NodeKind.Unary);
            m_Bw.Write((byte)ExpressionTypeToUnaryOp(u.NodeType));
            WriteVarInt(m_TypeReg.GetOrRegister(u.Type));
            WriteNode(u.Operand);
            m_Bw.Write(u.Method != null);
            if (u.Method != null) WriteMethod(u.Method);
        }

        private void WriteBinary(BinaryExpression b)
        {
            m_Bw.Write((byte)NodeKind.Binary);
            m_Bw.Write((byte)ExpressionTypeToBinaryOp(b.NodeType));
            WriteNode(b.Left);
            WriteNode(b.Right);
            m_Bw.Write(b.IsLiftedToNull);
            m_Bw.Write(b.Method != null);
            if (b.Method != null) WriteMethod(b.Method);
            m_Bw.Write(b.Conversion != null);
            if (b.Conversion != null) WriteNode(b.Conversion);
        }

        // ============================================================ reflection tokens

        private void WriteMethod(MethodInfo m)
        {
            WriteVarInt(m_TypeReg.GetOrRegister(m.DeclaringType));
            WriteString(m.Name);
            m_Bw.Write(m.IsStatic);
            var ps = m.GetParameters();
            WriteVarInt(ps.Length);
            foreach (var p in ps) WriteVarInt(m_TypeReg.GetOrRegister(p.ParameterType));
            if (m.IsGenericMethod)
            {
                var ga = m.GetGenericArguments();
                WriteVarInt(ga.Length);
                foreach (var t in ga) WriteVarInt(m_TypeReg.GetOrRegister(t));
            }
            else
            {
                WriteVarInt(0);
            }
        }

        private void WriteCtor(ConstructorInfo c)
        {
            WriteVarInt(m_TypeReg.GetOrRegister(c.DeclaringType));
            var ps = c.GetParameters();
            WriteVarInt(ps.Length);
            foreach (var p in ps) WriteVarInt(m_TypeReg.GetOrRegister(p.ParameterType));
        }

        private void WriteProperty(PropertyInfo p)
        {
            WriteVarInt(m_TypeReg.GetOrRegister(p.DeclaringType));
            WriteString(p.Name);
            var idx = p.GetIndexParameters();
            WriteVarInt(idx.Length);
            foreach (var ip in idx) WriteVarInt(m_TypeReg.GetOrRegister(ip.ParameterType));
        }

        private void WriteField(FieldInfo f)
        {
            WriteVarInt(m_TypeReg.GetOrRegister(f.DeclaringType));
            WriteString(f.Name);
        }

        // ============================================================ identity tables

        private int IdForParam(ParameterExpression p)
        {
            if (!m_ParamIds.TryGetValue(p, out var id))
            {
                id = m_ParamIds.Count;
                m_ParamIds[p] = id;
            }
            return id;
        }

        private int IdForLabel(LabelTarget t)
        {
            if (!m_LabelIds.TryGetValue(t, out var id))
            {
                id = m_LabelIds.Count;
                m_LabelIds[t] = id;
            }
            return id;
        }

        private void WriteOptionalLabel(LabelTarget t)
        {
            m_Bw.Write(t != null);
            if (t != null) WriteLabelTarget(t);
        }

        private void WriteLabelTarget(LabelTarget t)
        {
            WriteVarInt(IdForLabel(t));
            WriteVarInt(m_TypeReg.GetOrRegister(t.Type));
            WriteString(t.Name);
        }

        // ============================================================ primitives

        private void WriteString(string s)
        {
            if (s == null) { WriteVarInt(-1); return; }
            var bytes = Encoding.UTF8.GetBytes(s);
            WriteVarInt(bytes.Length);
            if (bytes.Length > 0) m_Bw.Write(bytes);
        }

        // Public for use by LiteWireReader's varint parity tests; semantics match
        // BinaryWriter.Write7BitEncodedInt but we cannot rely on that being public
        // in netstandard2.1.
        internal void WriteVarInt(int value)
        {
            uint v = unchecked((uint)value);
            while (v >= 0x80)
            {
                m_Bw.Write((byte)(v | 0x80));
                v >>= 7;
            }
            m_Bw.Write((byte)v);
        }

        internal void WriteVarInt64(long value)
        {
            ulong v = unchecked((ulong)value);
            while (v >= 0x80)
            {
                m_Bw.Write((byte)(v | 0x80));
                v >>= 7;
            }
            m_Bw.Write((byte)v);
        }

        // ============================================================ mapping helpers

        internal static bool IsUnary(ExpressionType t)
        {
            switch (t)
            {
                case ExpressionType.Convert:
                case ExpressionType.ConvertChecked:
                case ExpressionType.TypeAs:
                case ExpressionType.Throw:
                case ExpressionType.Quote:
                case ExpressionType.Unbox:
                case ExpressionType.ArrayLength:
                case ExpressionType.Negate:
                case ExpressionType.NegateChecked:
                case ExpressionType.UnaryPlus:
                case ExpressionType.Not:
                case ExpressionType.OnesComplement:
                case ExpressionType.Increment:
                case ExpressionType.Decrement:
                case ExpressionType.PreIncrementAssign:
                case ExpressionType.PreDecrementAssign:
                case ExpressionType.PostIncrementAssign:
                case ExpressionType.PostDecrementAssign:
                case ExpressionType.IsTrue:
                case ExpressionType.IsFalse:
                    return true;
                default: return false;
            }
        }

        internal static bool IsBinary(ExpressionType t)
        {
            switch (t)
            {
                case ExpressionType.Add: case ExpressionType.AddChecked:
                case ExpressionType.Subtract: case ExpressionType.SubtractChecked:
                case ExpressionType.Multiply: case ExpressionType.MultiplyChecked:
                case ExpressionType.Divide: case ExpressionType.Modulo: case ExpressionType.Power:
                case ExpressionType.And: case ExpressionType.Or: case ExpressionType.ExclusiveOr:
                case ExpressionType.LeftShift: case ExpressionType.RightShift:
                case ExpressionType.AndAlso: case ExpressionType.OrElse:
                case ExpressionType.Equal: case ExpressionType.NotEqual:
                case ExpressionType.LessThan: case ExpressionType.LessThanOrEqual:
                case ExpressionType.GreaterThan: case ExpressionType.GreaterThanOrEqual:
                case ExpressionType.Coalesce: case ExpressionType.ArrayIndex:
                case ExpressionType.Assign:
                case ExpressionType.AddAssign: case ExpressionType.SubtractAssign:
                case ExpressionType.MultiplyAssign: case ExpressionType.DivideAssign:
                case ExpressionType.ModuloAssign:
                case ExpressionType.AndAssign: case ExpressionType.OrAssign: case ExpressionType.ExclusiveOrAssign:
                case ExpressionType.LeftShiftAssign: case ExpressionType.RightShiftAssign:
                case ExpressionType.PowerAssign:
                    return true;
                default: return false;
            }
        }

        internal static UnaryOp ExpressionTypeToUnaryOp(ExpressionType t)
        {
            switch (t)
            {
                case ExpressionType.Convert:             return UnaryOp.Convert;
                case ExpressionType.ConvertChecked:      return UnaryOp.ConvertChecked;
                case ExpressionType.TypeAs:              return UnaryOp.TypeAs;
                case ExpressionType.Throw:               return UnaryOp.Throw;
                case ExpressionType.Quote:               return UnaryOp.Quote;
                case ExpressionType.Unbox:               return UnaryOp.Unbox;
                case ExpressionType.ArrayLength:         return UnaryOp.ArrayLength;
                case ExpressionType.Negate:              return UnaryOp.Negate;
                case ExpressionType.NegateChecked:       return UnaryOp.NegateChecked;
                case ExpressionType.UnaryPlus:           return UnaryOp.UnaryPlus;
                case ExpressionType.Not:                 return UnaryOp.Not;
                case ExpressionType.OnesComplement:      return UnaryOp.OnesComplement;
                case ExpressionType.Increment:           return UnaryOp.Increment;
                case ExpressionType.Decrement:           return UnaryOp.Decrement;
                case ExpressionType.PreIncrementAssign:  return UnaryOp.PreIncrementAssign;
                case ExpressionType.PreDecrementAssign:  return UnaryOp.PreDecrementAssign;
                case ExpressionType.PostIncrementAssign: return UnaryOp.PostIncrementAssign;
                case ExpressionType.PostDecrementAssign: return UnaryOp.PostDecrementAssign;
                case ExpressionType.IsTrue:              return UnaryOp.IsTrue;
                case ExpressionType.IsFalse:             return UnaryOp.IsFalse;
                default: throw new LiteWireException("E_LITE_UNSUPPORTED_UNARY", $"unsupported unary op {t}");
            }
        }

        internal static BinaryOp ExpressionTypeToBinaryOp(ExpressionType t)
        {
            switch (t)
            {
                case ExpressionType.Add:                return BinaryOp.Add;
                case ExpressionType.AddChecked:         return BinaryOp.AddChecked;
                case ExpressionType.Subtract:           return BinaryOp.Subtract;
                case ExpressionType.SubtractChecked:    return BinaryOp.SubtractChecked;
                case ExpressionType.Multiply:           return BinaryOp.Multiply;
                case ExpressionType.MultiplyChecked:    return BinaryOp.MultiplyChecked;
                case ExpressionType.Divide:             return BinaryOp.Divide;
                case ExpressionType.Modulo:             return BinaryOp.Modulo;
                case ExpressionType.Power:              return BinaryOp.Power;
                case ExpressionType.And:                return BinaryOp.And;
                case ExpressionType.Or:                 return BinaryOp.Or;
                case ExpressionType.ExclusiveOr:        return BinaryOp.ExclusiveOr;
                case ExpressionType.LeftShift:          return BinaryOp.LeftShift;
                case ExpressionType.RightShift:         return BinaryOp.RightShift;
                case ExpressionType.AndAlso:            return BinaryOp.AndAlso;
                case ExpressionType.OrElse:             return BinaryOp.OrElse;
                case ExpressionType.Equal:              return BinaryOp.Equal;
                case ExpressionType.NotEqual:           return BinaryOp.NotEqual;
                case ExpressionType.LessThan:           return BinaryOp.LessThan;
                case ExpressionType.LessThanOrEqual:    return BinaryOp.LessThanOrEqual;
                case ExpressionType.GreaterThan:        return BinaryOp.GreaterThan;
                case ExpressionType.GreaterThanOrEqual: return BinaryOp.GreaterThanOrEqual;
                case ExpressionType.Coalesce:           return BinaryOp.Coalesce;
                case ExpressionType.ArrayIndex:         return BinaryOp.ArrayIndex;
                case ExpressionType.Assign:             return BinaryOp.Assign;
                case ExpressionType.AddAssign:          return BinaryOp.AddAssign;
                case ExpressionType.SubtractAssign:     return BinaryOp.SubtractAssign;
                case ExpressionType.MultiplyAssign:     return BinaryOp.MultiplyAssign;
                case ExpressionType.DivideAssign:       return BinaryOp.DivideAssign;
                case ExpressionType.ModuloAssign:       return BinaryOp.ModuloAssign;
                case ExpressionType.AndAssign:          return BinaryOp.AndAssign;
                case ExpressionType.OrAssign:           return BinaryOp.OrAssign;
                case ExpressionType.ExclusiveOrAssign:  return BinaryOp.ExclusiveOrAssign;
                case ExpressionType.LeftShiftAssign:    return BinaryOp.LeftShiftAssign;
                case ExpressionType.RightShiftAssign:   return BinaryOp.RightShiftAssign;
                case ExpressionType.PowerAssign:        return BinaryOp.PowerAssign;
                default: throw new LiteWireException("E_LITE_UNSUPPORTED_BINARY", $"unsupported binary op {t}");
            }
        }

        internal static ExpressionType UnaryOpToExpressionType(UnaryOp op)
        {
            switch (op)
            {
                case UnaryOp.Convert:             return ExpressionType.Convert;
                case UnaryOp.ConvertChecked:      return ExpressionType.ConvertChecked;
                case UnaryOp.TypeAs:              return ExpressionType.TypeAs;
                case UnaryOp.Throw:               return ExpressionType.Throw;
                case UnaryOp.Quote:               return ExpressionType.Quote;
                case UnaryOp.Unbox:               return ExpressionType.Unbox;
                case UnaryOp.ArrayLength:         return ExpressionType.ArrayLength;
                case UnaryOp.Negate:              return ExpressionType.Negate;
                case UnaryOp.NegateChecked:       return ExpressionType.NegateChecked;
                case UnaryOp.UnaryPlus:           return ExpressionType.UnaryPlus;
                case UnaryOp.Not:                 return ExpressionType.Not;
                case UnaryOp.OnesComplement:      return ExpressionType.OnesComplement;
                case UnaryOp.Increment:           return ExpressionType.Increment;
                case UnaryOp.Decrement:           return ExpressionType.Decrement;
                case UnaryOp.PreIncrementAssign:  return ExpressionType.PreIncrementAssign;
                case UnaryOp.PreDecrementAssign:  return ExpressionType.PreDecrementAssign;
                case UnaryOp.PostIncrementAssign: return ExpressionType.PostIncrementAssign;
                case UnaryOp.PostDecrementAssign: return ExpressionType.PostDecrementAssign;
                case UnaryOp.IsTrue:              return ExpressionType.IsTrue;
                case UnaryOp.IsFalse:             return ExpressionType.IsFalse;
                default: throw new LiteWireException("E_LITE_WIRE_BAD_UNARY_OP", $"unknown UnaryOp byte 0x{(byte)op:X2}");
            }
        }

        internal static ExpressionType BinaryOpToExpressionType(BinaryOp op)
        {
            switch (op)
            {
                case BinaryOp.Add:                return ExpressionType.Add;
                case BinaryOp.AddChecked:         return ExpressionType.AddChecked;
                case BinaryOp.Subtract:           return ExpressionType.Subtract;
                case BinaryOp.SubtractChecked:    return ExpressionType.SubtractChecked;
                case BinaryOp.Multiply:           return ExpressionType.Multiply;
                case BinaryOp.MultiplyChecked:    return ExpressionType.MultiplyChecked;
                case BinaryOp.Divide:             return ExpressionType.Divide;
                case BinaryOp.Modulo:             return ExpressionType.Modulo;
                case BinaryOp.Power:              return ExpressionType.Power;
                case BinaryOp.And:                return ExpressionType.And;
                case BinaryOp.Or:                 return ExpressionType.Or;
                case BinaryOp.ExclusiveOr:        return ExpressionType.ExclusiveOr;
                case BinaryOp.LeftShift:          return ExpressionType.LeftShift;
                case BinaryOp.RightShift:         return ExpressionType.RightShift;
                case BinaryOp.AndAlso:            return ExpressionType.AndAlso;
                case BinaryOp.OrElse:             return ExpressionType.OrElse;
                case BinaryOp.Equal:              return ExpressionType.Equal;
                case BinaryOp.NotEqual:           return ExpressionType.NotEqual;
                case BinaryOp.LessThan:           return ExpressionType.LessThan;
                case BinaryOp.LessThanOrEqual:    return ExpressionType.LessThanOrEqual;
                case BinaryOp.GreaterThan:        return ExpressionType.GreaterThan;
                case BinaryOp.GreaterThanOrEqual: return ExpressionType.GreaterThanOrEqual;
                case BinaryOp.Coalesce:           return ExpressionType.Coalesce;
                case BinaryOp.ArrayIndex:         return ExpressionType.ArrayIndex;
                case BinaryOp.Assign:             return ExpressionType.Assign;
                case BinaryOp.AddAssign:          return ExpressionType.AddAssign;
                case BinaryOp.SubtractAssign:     return ExpressionType.SubtractAssign;
                case BinaryOp.MultiplyAssign:     return ExpressionType.MultiplyAssign;
                case BinaryOp.DivideAssign:       return ExpressionType.DivideAssign;
                case BinaryOp.ModuloAssign:       return ExpressionType.ModuloAssign;
                case BinaryOp.AndAssign:          return ExpressionType.AndAssign;
                case BinaryOp.OrAssign:           return ExpressionType.OrAssign;
                case BinaryOp.ExclusiveOrAssign:  return ExpressionType.ExclusiveOrAssign;
                case BinaryOp.LeftShiftAssign:    return ExpressionType.LeftShiftAssign;
                case BinaryOp.RightShiftAssign:   return ExpressionType.RightShiftAssign;
                case BinaryOp.PowerAssign:        return ExpressionType.PowerAssign;
                default: throw new LiteWireException("E_LITE_WIRE_BAD_BINARY_OP", $"unknown BinaryOp byte 0x{(byte)op:X2}");
            }
        }
    }
}
