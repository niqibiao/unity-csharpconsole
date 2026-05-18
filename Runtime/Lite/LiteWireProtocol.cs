namespace Zh1Zh1.CSharpConsole.Lite
{
    // Lite mode Expression-tree wire format.
    //
    // Hand-written binary tagged-union (Docs~/ExpressionInterpreterFeasibility_zh.md §3.1, v3).
    // Decision recorded 2026-05-13: zero external dependency, IL2CPP-safe, ~80% wire reduction.
    //
    // Layout: [NodeKind: byte][payload...]
    //   - integers via BinaryWriter.Write7BitEncodedInt (varint)
    //   - strings via BinaryWriter.Write(string) (7-bit length-prefixed UTF-8)
    //   - floats via BinaryWriter (IEEE 754 little-endian)
    //
    // Enum values are explicit and append-only — never renumber.

    internal static class LiteWireProtocol
    {
        public const int Version = 3;
    }

    internal enum NodeKind : byte
    {
        Unknown        = 0x00,

        Constant       = 0x01,
        Parameter      = 0x02,
        SlotsRef       = 0x03,
        Lambda         = 0x04,
        Invoke         = 0x05,
        Call           = 0x06,
        MemberAccess   = 0x07,
        Block          = 0x08,
        Conditional    = 0x09,
        Default        = 0x0A,
        New            = 0x0B,
        NewArrayInit   = 0x0C,
        NewArrayBounds = 0x0D,
        TypeIs         = 0x0E,
        TypeEqual      = 0x0F,
        Loop           = 0x10,
        Goto           = 0x11,
        Label          = 0x12,
        Try            = 0x13,
        Switch         = 0x14,
        Index          = 0x15,
        MemberInit     = 0x16,
        ListInit       = 0x17,

        Unary          = 0x18,
        Binary         = 0x19,
    }

    internal enum UnaryOp : byte
    {
        Convert             = 0x01,
        ConvertChecked      = 0x02,
        TypeAs              = 0x03,
        Throw               = 0x04,
        Quote               = 0x05,
        Unbox               = 0x06,
        ArrayLength         = 0x07,
        Negate              = 0x08,
        NegateChecked       = 0x09,
        UnaryPlus           = 0x0A,
        Not                 = 0x0B,
        OnesComplement      = 0x0C,
        Increment           = 0x0D,
        Decrement           = 0x0E,
        PreIncrementAssign  = 0x0F,
        PreDecrementAssign  = 0x10,
        PostIncrementAssign = 0x11,
        PostDecrementAssign = 0x12,
        IsTrue              = 0x13,
        IsFalse             = 0x14,
    }

    internal enum BinaryOp : byte
    {
        Add                = 0x01,
        AddChecked         = 0x02,
        Subtract           = 0x03,
        SubtractChecked    = 0x04,
        Multiply           = 0x05,
        MultiplyChecked    = 0x06,
        Divide             = 0x07,
        Modulo             = 0x08,
        Power              = 0x09,
        And                = 0x0A,
        Or                 = 0x0B,
        ExclusiveOr        = 0x0C,
        LeftShift          = 0x0D,
        RightShift         = 0x0E,
        AndAlso            = 0x0F,
        OrElse             = 0x10,
        Equal              = 0x11,
        NotEqual           = 0x12,
        LessThan           = 0x13,
        LessThanOrEqual    = 0x14,
        GreaterThan        = 0x15,
        GreaterThanOrEqual = 0x16,
        Coalesce           = 0x17,
        ArrayIndex         = 0x18,
        Assign             = 0x19,
        AddAssign          = 0x1A,
        SubtractAssign     = 0x1B,
        MultiplyAssign     = 0x1C,
        DivideAssign       = 0x1D,
        ModuloAssign       = 0x1E,
        AndAssign          = 0x1F,
        OrAssign           = 0x20,
        ExclusiveOrAssign  = 0x21,
        LeftShiftAssign    = 0x22,
        RightShiftAssign   = 0x23,
        PowerAssign        = 0x24,
    }

    // Constant value payload discriminator. Constant nodes carry one of these
    // after the typeId, then the value bytes follow in a kind-specific layout:
    //   Null     — no payload bytes
    //   Bool     — 1 byte
    //   I8/U8    — 1 byte
    //   I16/U16  — 2 bytes
    //   I32/U32  — varint (signed: zigzag handled by codec)
    //   I64/U64  — varint
    //   F32      — 4 bytes (IEEE 754)
    //   F64      — 8 bytes (IEEE 754)
    //   Decimal  — 16 bytes (4× int32 from decimal.GetBits)
    //   Char     — varint
    //   Str      — length-prefixed UTF-8
    //   Type     — typeId varint (refers to the captured Type, distinct from
    //              the Constant's own runtime Type)
    //   Enum     — typeId varint (enum underlying type) + varint (numeric value)
    internal enum ValueKind : byte
    {
        Null     = 0x00,
        Bool     = 0x01,
        I8       = 0x02,
        U8       = 0x03,
        I16      = 0x04,
        U16      = 0x05,
        I32      = 0x06,
        U32      = 0x07,
        I64      = 0x08,
        U64      = 0x09,
        F32      = 0x0A,
        F64      = 0x0B,
        Decimal  = 0x0C,
        Char     = 0x0D,
        Str      = 0x0E,
        Type     = 0x0F,
        Enum     = 0x10,
    }
}
