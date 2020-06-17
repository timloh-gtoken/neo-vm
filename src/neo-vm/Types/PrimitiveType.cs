using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Neo.VM.Types
{
    public abstract class PrimitiveType : StackItem
    {
        internal abstract ReadOnlyMemory<byte> Memory { get; }
        public virtual int Size => Memory.Length;
        public ReadOnlySpan<byte> Span => Memory.Span;

        public override StackItem ConvertTo(StackItemType type)
        {
            if (type == Type) return this;
            return type switch
            {
                StackItemType.Integer => ToBigInteger(),
                StackItemType.ByteString => Memory,
                StackItemType.Buffer => new Buffer(Span),
                _ => base.ConvertTo(type)
            };
        }

        internal sealed override StackItem DeepCopy(Dictionary<CompoundType, CompoundType> refMap)
        {
            return this;
        }

        public abstract override bool Equals(StackItem other);

        public abstract override int GetHashCode();

        public virtual BigInteger ToBigInteger()
        {
            if (Size > Integer.MaxSize) throw new InvalidCastException();
            return new BigInteger(Span);
        }

        public override bool ToBoolean()
        {
            if (Size > Integer.MaxSize) return true;
            return Unsafe.NotZero(Span);
        }

        public int ToInt32()
        {
            BigInteger i = ToBigInteger();
            if (i < int.MinValue || i > int.MaxValue) throw new InvalidCastException();
            return (int)i;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PrimitiveType(int value)
        {
            return (Integer)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PrimitiveType(uint value)
        {
            return (Integer)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PrimitiveType(long value)
        {
            return (Integer)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PrimitiveType(ulong value)
        {
            return (Integer)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PrimitiveType(BigInteger value)
        {
            return (Integer)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PrimitiveType(bool value)
        {
            return (Boolean)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PrimitiveType(byte[] value)
        {
            return (ByteString)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PrimitiveType(ReadOnlyMemory<byte> value)
        {
            return (ByteString)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PrimitiveType(string value)
        {
            return (ByteString)value;
        }
    }
}
