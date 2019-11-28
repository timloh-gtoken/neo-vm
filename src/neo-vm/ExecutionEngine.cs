using Neo.VM.Types;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using VMArray = Neo.VM.Types.Array;

namespace Neo.VM
{
    public class ExecutionEngine : IDisposable
    {
        #region Limits Variables

        /// <summary>
        /// Max value for SHL and SHR
        /// </summary>
        public virtual int Max_SHL_SHR => 256;

        /// <summary>
        /// Min value for SHL and SHR
        /// </summary>
        public virtual int Min_SHL_SHR => -256;

        /// <summary>
        /// The max size in bytes allowed size for BigInteger
        /// </summary>
        public const int MaxSizeForBigInteger = 32;

        /// <summary>
        /// Set the max Stack Size
        /// </summary>
        public virtual uint MaxStackSize => 2 * 1024;

        /// <summary>
        /// Set Max Item Size
        /// </summary>
        public virtual uint MaxItemSize => 1024 * 1024;

        /// <summary>
        /// Set Max Invocation Stack Size
        /// </summary>
        public virtual uint MaxInvocationStackSize => 1024;

        /// <summary>
        /// Set Max Array Size
        /// </summary>
        public virtual uint MaxArraySize => 1024;

        #endregion

        private class ReferenceTracing
        {
            public int StackReferences;
            public Dictionary<CompoundType, int> ObjectReferences;
        }

        private readonly Dictionary<CompoundType, ReferenceTracing> reference_tracing = new Dictionary<CompoundType, ReferenceTracing>(ReferenceEqualityComparer.Default);
        private readonly List<CompoundType> zero_referred = new List<CompoundType>();
        private int stackitem_count = 0;

        public RandomAccessStack<ExecutionContext> InvocationStack { get; } = new RandomAccessStack<ExecutionContext>();
        public RandomAccessStack<StackItem> ResultStack { get; } = new RandomAccessStack<StackItem>();

        public ExecutionContext CurrentContext => InvocationStack.Count > 0 ? InvocationStack.Peek() : null;
        public ExecutionContext EntryContext => InvocationStack.Count > 0 ? InvocationStack.Peek(InvocationStack.Count - 1) : null;
        public VMState State { get; internal protected set; } = VMState.BREAK;
        public int StackItemCount => stackitem_count;

        #region Limits

        /// <summary>
        /// Check if it is possible to overflow the MaxArraySize
        /// </summary>
        /// <param name="length">Length</param>
        /// <returns>Return True if are allowed, otherwise False</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CheckArraySize(int length) => length <= MaxArraySize;

        /// <summary>
        /// Check if the is possible to overflow the MaxItemSize
        /// </summary>
        /// <param name="length">Length</param>
        /// <returns>Return True if are allowed, otherwise False</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CheckMaxItemSize(int length) => length >= 0 && length <= MaxItemSize;

        /// <summary>
        /// Check if the BigInteger is allowed for numeric operations
        /// </summary>
        /// <param name="value">Value</param>
        /// <returns>Return True if are allowed, otherwise False</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CheckBigInteger(BigInteger value) => value.GetByteCount() <= MaxSizeForBigInteger;

        /// <summary>
        /// Check if the number is allowed from SHL and SHR
        /// </summary>
        /// <param name="shift">Shift</param>
        /// <returns>Return True if are allowed, otherwise False</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CheckShift(int shift) => shift <= Max_SHL_SHR && shift >= Min_SHL_SHR;

        #endregion

        private void AddReference(StackItem referred, CompoundType parent)
        {
            if (!(referred is CompoundType compound)) return;
            if (!reference_tracing.TryGetValue(compound, out ReferenceTracing tracing))
            {
                tracing = new ReferenceTracing();
                reference_tracing.Add(compound, tracing);
            }
            if (tracing.ObjectReferences is null)
                tracing.ObjectReferences = new Dictionary<CompoundType, int>(ReferenceEqualityComparer.Default);
            if (tracing.ObjectReferences.TryGetValue(parent, out int count))
                count++;
            else
                count = 1;
            tracing.ObjectReferences[parent] = count;
        }

        public bool AppendItem(VMArray array, StackItem item)
        {
            if (array.Count >= MaxArraySize) return false;
            array.Add(item);
            stackitem_count++;
            AddReference(item, array);
            return true;
        }

        private void CheckZeroReferred()
        {
            if (zero_referred.Count == 0) return;
            HashSet<CompoundType> toBeDestroyed = new HashSet<CompoundType>(ReferenceEqualityComparer.Default);
            foreach (CompoundType compound in zero_referred)
            {
                HashSet<CompoundType> toBeDestroyedInLoop = new HashSet<CompoundType>(ReferenceEqualityComparer.Default);
                Queue<CompoundType> toBeChecked = new Queue<CompoundType>();
                toBeChecked.Enqueue(compound);
                while (toBeChecked.Count > 0)
                {
                    CompoundType c = toBeChecked.Dequeue();
                    ReferenceTracing tracing = reference_tracing[c];
                    if (tracing.StackReferences > 0)
                    {
                        toBeDestroyedInLoop.Clear();
                        break;
                    }
                    toBeDestroyedInLoop.Add(c);
                    if (tracing.ObjectReferences is null) continue;
                    foreach (var pair in tracing.ObjectReferences)
                        if (pair.Value > 0 && !toBeDestroyed.Contains(pair.Key) && !toBeDestroyedInLoop.Contains(pair.Key))
                            toBeChecked.Enqueue(pair.Key);
                }
                if (toBeDestroyedInLoop.Count > 0)
                    toBeDestroyed.UnionWith(toBeDestroyedInLoop);
            }
            foreach (CompoundType compound in toBeDestroyed)
            {
                reference_tracing.Remove(compound);
                if (compound is Map)
                    stackitem_count -= compound.Count * 2;
                else
                    stackitem_count -= compound.Count;
            }
            zero_referred.Clear();
        }

        protected virtual void ContextUnloaded(ExecutionContext context)
        {
        }

        private Struct CreateClonedStruct(Struct s)
        {
            Struct result = new Struct();
            Queue<Struct> queue = new Queue<Struct>();
            queue.Enqueue(result);
            queue.Enqueue(s);
            while (queue.Count > 0)
            {
                Struct a = queue.Dequeue();
                Struct b = queue.Dequeue();
                foreach (StackItem item in b)
                {
                    if (item is Struct sb)
                    {
                        Struct sa = new Struct();
                        AppendItem(a, sa);
                        queue.Enqueue(sa);
                        queue.Enqueue(sb);
                    }
                    else
                    {
                        AppendItem(a, item);
                    }
                }
            }
            return result;
        }

        public virtual void Dispose()
        {
            InvocationStack.Clear();
        }

        public VMState Execute()
        {
            if (State == VMState.BREAK)
                State = VMState.NONE;
            while (State != VMState.HALT && State != VMState.FAULT)
                ExecuteNext();
            return State;
        }

        internal protected void ExecuteNext()
        {
            if (InvocationStack.Count == 0)
            {
                State = VMState.HALT;
            }
            else
            {
                try
                {
                    Instruction instruction = CurrentContext.CurrentInstruction;
                    if (!PreExecuteInstruction() || !ExecuteInstruction() || !PostExecuteInstruction(instruction))
                        State = VMState.FAULT;
                }
                catch
                {
                    State = VMState.FAULT;
                }
            }
        }

        private bool ExecuteInstruction()
        {
            ExecutionContext context = CurrentContext;
            Instruction instruction = context.CurrentInstruction;
            if (instruction.OpCode >= OpCode.PUSHBYTES1 && instruction.OpCode <= OpCode.PUSHDATA4)
            {
                if (!CheckMaxItemSize(instruction.Operand.Length)) return false;
                Push(instruction.Operand);
            }
            else switch (instruction.OpCode)
                {
                    // Push value
                    case OpCode.PUSH0:
                        {
                            Push(ReadOnlyMemory<byte>.Empty);
                            break;
                        }
                    case OpCode.PUSHM1:
                    case OpCode.PUSH1:
                    case OpCode.PUSH2:
                    case OpCode.PUSH3:
                    case OpCode.PUSH4:
                    case OpCode.PUSH5:
                    case OpCode.PUSH6:
                    case OpCode.PUSH7:
                    case OpCode.PUSH8:
                    case OpCode.PUSH9:
                    case OpCode.PUSH10:
                    case OpCode.PUSH11:
                    case OpCode.PUSH12:
                    case OpCode.PUSH13:
                    case OpCode.PUSH14:
                    case OpCode.PUSH15:
                    case OpCode.PUSH16:
                        {
                            Push((int)instruction.OpCode - (int)OpCode.PUSH1 + 1);
                            break;
                        }
                    case OpCode.PUSHNULL:
                        {
                            Push(StackItem.Null);
                            break;
                        }

                    // Control
                    case OpCode.NOP: break;
                    case OpCode.JMP:
                    case OpCode.JMPIF:
                    case OpCode.JMPIFNOT:
                        {
                            int offset = context.InstructionPointer + instruction.TokenI16;
                            if (offset < 0 || offset > context.Script.Length) return false;
                            bool fValue = true;
                            if (instruction.OpCode > OpCode.JMP)
                            {
                                if (!TryPop(out StackItem x)) return false;
                                fValue = x.ToBoolean();
                                if (instruction.OpCode == OpCode.JMPIFNOT)
                                    fValue = !fValue;
                            }
                            if (fValue)
                                context.InstructionPointer = offset;
                            else
                                context.InstructionPointer += 3;
                            return true;
                        }
                    case OpCode.CALL:
                        {
                            ExecutionContext context_call = context.Clone();
                            context_call.InstructionPointer = context.InstructionPointer + instruction.TokenI16;
                            if (context_call.InstructionPointer < 0 || context_call.InstructionPointer > context_call.Script.Length) return false;
                            LoadContext(context_call);
                            break;
                        }
                    case OpCode.RET:
                        {
                            ExecutionContext context_pop = InvocationStack.Pop();
                            int rvcount = context_pop.RVCount;
                            if (rvcount == -1) rvcount = context_pop.EvaluationStack.Count;
                            RandomAccessStack<StackItem> stack_eval;
                            if (InvocationStack.Count == 0)
                                stack_eval = ResultStack;
                            else
                                stack_eval = CurrentContext.EvaluationStack;
                            if (context_pop.EvaluationStack == stack_eval)
                            {
                                if (context_pop.RVCount != 0) return false;
                            }
                            else
                            {
                                if (context_pop.EvaluationStack.Count != rvcount) return false;
                                if (rvcount > 0)
                                    context_pop.EvaluationStack.CopyTo(stack_eval);
                            }
                            if (context_pop.RVCount == -1 && InvocationStack.Count > 0)
                            {
                                context_pop.AltStack.CopyTo(CurrentContext.AltStack);
                            }
                            if (InvocationStack.Count == 0)
                            {
                                State = VMState.HALT;
                            }
                            ContextUnloaded(context_pop);
                            return true;
                        }
                    case OpCode.SYSCALL:
                        {
                            if (!OnSysCall(instruction.TokenU32))
                                return false;
                            break;
                        }

                    // Stack ops
                    case OpCode.DUPFROMALTSTACKBOTTOM:
                        {
                            Push(context.AltStack.Peek(-1));
                            break;
                        }
                    case OpCode.DUPFROMALTSTACK:
                        {
                            Push(context.AltStack.Peek());
                            break;
                        }
                    case OpCode.TOALTSTACK:
                        {
                            //Move item from the EvaluationStack to the AltStack. No need to check.
                            context.AltStack.Push(context.EvaluationStack.Pop());
                            break;
                        }
                    case OpCode.FROMALTSTACK:
                        {
                            //Move item from the AltStack to the EvaluationStack. No need to check.
                            context.EvaluationStack.Push(context.AltStack.Pop());
                            break;
                        }
                    case OpCode.ISNULL:
                        {
                            if (!TryPop(out StackItem x)) return false;
                            Push(x.IsNull);
                            break;
                        }
                    case OpCode.XDROP:
                        {
                            if (!TryPop(out PrimitiveType item_n)) return false;
                            int n = (int)item_n.ToBigInteger();
                            if (n < 0) return false;
                            if (!TryRemove(n, out StackItem _)) return false;
                            break;
                        }
                    case OpCode.XSWAP:
                        {
                            if (!TryPop(out PrimitiveType item_n)) return false;
                            int n = (int)item_n.ToBigInteger();
                            if (n < 0) return false;
                            if (n == 0) break;
                            //Swap item[0] and item[n]. No need to check.
                            StackItem xn = context.EvaluationStack.Peek(n);
                            context.EvaluationStack.Set(n, context.EvaluationStack.Peek());
                            context.EvaluationStack.Set(0, xn);
                            break;
                        }
                    case OpCode.XTUCK:
                        {
                            if (!TryPop(out PrimitiveType item_n)) return false;
                            int n = (int)item_n.ToBigInteger();
                            if (n <= 0) return false;
                            if (!TryInsert(n, context.EvaluationStack.Peek())) return false;
                            break;
                        }
                    case OpCode.DEPTH:
                        {
                            Push(context.EvaluationStack.Count);
                            break;
                        }
                    case OpCode.DROP:
                        {
                            if (!TryPop(out StackItem _)) return false;
                            break;
                        }
                    case OpCode.DUP:
                        {
                            Push(context.EvaluationStack.Peek());
                            break;
                        }
                    case OpCode.NIP:
                        {
                            if (!TryRemove(1, out StackItem _)) return false;
                            break;
                        }
                    case OpCode.OVER:
                        {
                            Push(context.EvaluationStack.Peek(1));
                            break;
                        }
                    case OpCode.PICK:
                        {
                            if (!TryPop(out PrimitiveType item_n)) return false;
                            int n = (int)item_n.ToBigInteger();
                            if (n < 0) return false;
                            Push(context.EvaluationStack.Peek(n));
                            break;
                        }
                    case OpCode.ROLL:
                        {
                            if (!TryPop(out PrimitiveType item_n)) return false;
                            int n = (int)item_n.ToBigInteger();
                            if (n < 0) return false;
                            if (n == 0) break;
                            //Move item[n] to the top. No need to check.
                            context.EvaluationStack.Push(context.EvaluationStack.Remove(n));
                            break;
                        }
                    case OpCode.ROT:
                        {
                            //Move item[2] to the top. No need to check.
                            context.EvaluationStack.Push(context.EvaluationStack.Remove(2));
                            break;
                        }
                    case OpCode.SWAP:
                        {
                            //Move item[1] to the top. No need to check.
                            context.EvaluationStack.Push(context.EvaluationStack.Remove(1));
                            break;
                        }
                    case OpCode.TUCK:
                        {
                            if (!TryInsert(2, context.EvaluationStack.Peek())) return false;
                            break;
                        }
                    case OpCode.CAT:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            ReadOnlyMemory<byte> x2 = item_x2.ToMemory();
                            ReadOnlyMemory<byte> x1 = item_x1.ToMemory();
                            StackItem result;
                            if (x1.IsEmpty)
                            {
                                result = x2;
                            }
                            else if (x2.IsEmpty)
                            {
                                result = x1;
                            }
                            else
                            {
                                int length = x1.Length + x2.Length;
                                if (!CheckMaxItemSize(length)) return false;
                                byte[] dstBuffer = new byte[length];
                                x1.CopyTo(dstBuffer);
                                x2.CopyTo(dstBuffer.AsMemory(x1.Length));
                                result = dstBuffer;
                            }
                            Push(result);
                            break;
                        }
                    case OpCode.SUBSTR:
                        {
                            if (!TryPop(out PrimitiveType item_count)) return false;
                            int count = (int)item_count.ToBigInteger();
                            if (count < 0) return false;
                            if (count > MaxItemSize) count = (int)MaxItemSize;
                            if (!TryPop(out PrimitiveType item_index)) return false;
                            int index = (int)item_index.ToBigInteger();
                            if (index < 0) return false;
                            if (!TryPop(out PrimitiveType item_x)) return false;
                            ReadOnlyMemory<byte> x = item_x.ToMemory();
                            if (index > x.Length) return false;
                            if (index + count > x.Length) count = x.Length - index;
                            Push(x.Slice(index, count));
                            break;
                        }
                    case OpCode.LEFT:
                        {
                            if (!TryPop(out PrimitiveType item_count)) return false;
                            int count = (int)item_count.ToBigInteger();
                            if (count < 0) return false;
                            if (!TryPop(out PrimitiveType item_x)) return false;
                            ReadOnlyMemory<byte> x = item_x.ToMemory();
                            if (count < x.Length) x = x[0..count];
                            Push(x);
                            break;
                        }
                    case OpCode.RIGHT:
                        {
                            if (!TryPop(out PrimitiveType item_count)) return false;
                            int count = (int)item_count.ToBigInteger();
                            if (count < 0) return false;
                            if (!TryPop(out PrimitiveType item_x)) return false;
                            ReadOnlyMemory<byte> x = item_x.ToMemory();
                            if (count > x.Length) return false;
                            if (count < x.Length) x = x[^count..^0];
                            Push(x);
                            break;
                        }
                    case OpCode.SIZE:
                        {
                            if (!TryPop(out PrimitiveType x)) return false;
                            Push(x.GetByteLength());
                            break;
                        }

                    // Bitwise logic
                    case OpCode.INVERT:
                        {
                            if (!TryPop(out PrimitiveType item_x)) return false;
                            BigInteger x = item_x.ToBigInteger();
                            if (!CheckBigInteger(x)) return false;
                            Push(~x);
                            break;
                        }
                    case OpCode.AND:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            Push(x1 & x2);
                            break;
                        }
                    case OpCode.OR:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            Push(x1 | x2);
                            break;
                        }
                    case OpCode.XOR:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            Push(x1 ^ x2);
                            break;
                        }
                    case OpCode.EQUAL:
                        {
                            if (!TryPop(out StackItem x2)) return false;
                            if (!TryPop(out StackItem x1)) return false;
                            Push(x1.Equals(x2));
                            break;
                        }

                    // Numeric
                    case OpCode.INC:
                        {
                            if (!TryPop(out PrimitiveType item_x)) return false;
                            BigInteger x = item_x.ToBigInteger();
                            if (!CheckBigInteger(x)) return false;
                            x += 1;
                            if (!CheckBigInteger(x)) return false;
                            Push(x);
                            break;
                        }
                    case OpCode.DEC:
                        {
                            if (!TryPop(out PrimitiveType item_x)) return false;
                            BigInteger x = item_x.ToBigInteger();
                            if (!CheckBigInteger(x)) return false;
                            x -= 1;
                            if (!CheckBigInteger(x)) return false;
                            Push(x);
                            break;
                        }
                    case OpCode.SIGN:
                        {
                            if (!TryPop(out PrimitiveType item_x)) return false;
                            BigInteger x = item_x.ToBigInteger();
                            if (!CheckBigInteger(x)) return false;
                            Push(x.Sign);
                            break;
                        }
                    case OpCode.NEGATE:
                        {
                            if (!TryPop(out PrimitiveType item_x)) return false;
                            BigInteger x = item_x.ToBigInteger();
                            if (!CheckBigInteger(x)) return false;
                            Push(-x);
                            break;
                        }
                    case OpCode.ABS:
                        {
                            if (!TryPop(out PrimitiveType item_x)) return false;
                            BigInteger x = item_x.ToBigInteger();
                            if (!CheckBigInteger(x)) return false;
                            Push(BigInteger.Abs(x));
                            break;
                        }
                    case OpCode.NOT:
                        {
                            if (!TryPop(out StackItem x)) return false;
                            Push(!x.ToBoolean());
                            break;
                        }
                    case OpCode.NZ:
                        {
                            if (!TryPop(out PrimitiveType item_x)) return false;
                            BigInteger x = item_x.ToBigInteger();
                            if (!CheckBigInteger(x)) return false;
                            Push(!x.IsZero);
                            break;
                        }
                    case OpCode.ADD:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            BigInteger result = x1 + x2;
                            if (!CheckBigInteger(result)) return false;
                            Push(result);
                            break;
                        }
                    case OpCode.SUB:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            BigInteger result = x1 - x2;
                            if (!CheckBigInteger(result)) return false;
                            Push(result);
                            break;
                        }
                    case OpCode.MUL:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            BigInteger result = x1 * x2;
                            if (!CheckBigInteger(result)) return false;
                            Push(result);
                            break;
                        }
                    case OpCode.DIV:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            Push(x1 / x2);
                            break;
                        }
                    case OpCode.MOD:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            Push(x1 % x2);
                            break;
                        }
                    case OpCode.SHL:
                        {
                            if (!TryPop(out PrimitiveType item_shift)) return false;
                            int shift = (int)item_shift.ToBigInteger();
                            if (!CheckShift(shift)) return false;
                            if (shift == 0) break;
                            if (!TryPop(out PrimitiveType item_x)) return false;
                            BigInteger x = item_x.ToBigInteger();
                            if (!CheckBigInteger(x)) return false;
                            x <<= shift;
                            if (!CheckBigInteger(x)) return false;
                            Push(x);
                            break;
                        }
                    case OpCode.SHR:
                        {
                            if (!TryPop(out PrimitiveType item_shift)) return false;
                            int shift = (int)item_shift.ToBigInteger();
                            if (!CheckShift(shift)) return false;
                            if (shift == 0) break;
                            if (!TryPop(out PrimitiveType item_x)) return false;
                            BigInteger x = item_x.ToBigInteger();
                            if (!CheckBigInteger(x)) return false;
                            x >>= shift;
                            if (!CheckBigInteger(x)) return false;
                            Push(x);
                            break;
                        }
                    case OpCode.BOOLAND:
                        {
                            if (!TryPop(out StackItem x2)) return false;
                            if (!TryPop(out StackItem x1)) return false;
                            Push(x1.ToBoolean() && x2.ToBoolean());
                            break;
                        }
                    case OpCode.BOOLOR:
                        {
                            if (!TryPop(out StackItem x2)) return false;
                            if (!TryPop(out StackItem x1)) return false;
                            Push(x1.ToBoolean() || x2.ToBoolean());
                            break;
                        }
                    case OpCode.NUMEQUAL:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            Push(x1 == x2);
                            break;
                        }
                    case OpCode.NUMNOTEQUAL:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            Push(x1 != x2);
                            break;
                        }
                    case OpCode.LT:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            Push(x1 < x2);
                            break;
                        }
                    case OpCode.GT:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            Push(x1 > x2);
                            break;
                        }
                    case OpCode.LTE:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            Push(x1 <= x2);
                            break;
                        }
                    case OpCode.GTE:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            Push(x1 >= x2);
                            break;
                        }
                    case OpCode.MIN:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            Push(BigInteger.Min(x1, x2));
                            break;
                        }
                    case OpCode.MAX:
                        {
                            if (!TryPop(out PrimitiveType item_x2)) return false;
                            BigInteger x2 = item_x2.ToBigInteger();
                            if (!CheckBigInteger(x2)) return false;
                            if (!TryPop(out PrimitiveType item_x1)) return false;
                            BigInteger x1 = item_x1.ToBigInteger();
                            if (!CheckBigInteger(x1)) return false;
                            Push(BigInteger.Max(x1, x2));
                            break;
                        }
                    case OpCode.WITHIN:
                        {
                            if (!TryPop(out PrimitiveType item_b)) return false;
                            BigInteger b = item_b.ToBigInteger();
                            if (!CheckBigInteger(b)) return false;
                            if (!TryPop(out PrimitiveType item_a)) return false;
                            BigInteger a = item_a.ToBigInteger();
                            if (!CheckBigInteger(a)) return false;
                            if (!TryPop(out PrimitiveType item_x)) return false;
                            BigInteger x = item_x.ToBigInteger();
                            if (!CheckBigInteger(x)) return false;
                            Push(a <= x && x < b);
                            break;
                        }

                    // Array
                    case OpCode.ARRAYSIZE:
                        {
                            if (!TryPop(out StackItem x)) return false;
                            switch (x)
                            {
                                case CompoundType compound:
                                    Push(compound.Count);
                                    break;
                                case PrimitiveType primitive:
                                    Push(primitive.GetByteLength());
                                    break;
                                default:
                                    return false;
                            }
                            break;
                        }
                    case OpCode.PACK:
                        {
                            if (!TryPop(out PrimitiveType item_size)) return false;
                            int size = (int)item_size.ToBigInteger();
                            if (size < 0 || size > context.EvaluationStack.Count || !CheckArraySize(size))
                                return false;
                            VMArray array = new VMArray();
                            for (int i = 0; i < size; i++)
                            {
                                if (!TryPop(out StackItem item)) return false;
                                if (!AppendItem(array, item)) return false;
                            }
                            Push(array);
                            break;
                        }
                    case OpCode.UNPACK:
                        {
                            if (!TryPop(out VMArray array)) return false;
                            for (int i = array.Count - 1; i >= 0; i--)
                                Push(array[i]);
                            Push(array.Count);
                            break;
                        }
                    case OpCode.PICKITEM:
                        {
                            if (!TryPop(out PrimitiveType key)) return false;
                            if (!TryPop(out StackItem x)) return false;
                            switch (x)
                            {
                                case VMArray array:
                                    {
                                        int index = (int)key.ToBigInteger();
                                        if (index < 0 || index >= array.Count) return false;
                                        Push(array[index]);
                                        break;
                                    }
                                case Map map:
                                    {
                                        if (!map.TryGetValue(key, out StackItem value)) return false;
                                        Push(value);
                                        break;
                                    }
                                case PrimitiveType primitive:
                                    {
                                        ReadOnlySpan<byte> byteArray = primitive.ToByteArray();
                                        int index = (int)key.ToBigInteger();
                                        if (index < 0 || index >= byteArray.Length) return false;
                                        Push((BigInteger)byteArray[index]);
                                        break;
                                    }
                                default:
                                    return false;
                            }
                            break;
                        }
                    case OpCode.SETITEM:
                        {
                            if (!TryPop(out StackItem value)) return false;
                            if (value is Struct s) value = CreateClonedStruct(s);
                            if (!TryPop(out PrimitiveType key)) return false;
                            if (!TryPop(out StackItem x)) return false;
                            switch (x)
                            {
                                case VMArray array:
                                    {
                                        int index = (int)key.ToBigInteger();
                                        if (!SetItem(array, index, value)) return false;
                                        break;
                                    }
                                case Map map:
                                    {
                                        if (!SetItem(map, key, value)) return false;
                                        break;
                                    }
                                default:
                                    return false;
                            }
                            break;
                        }
                    case OpCode.NEWARRAY:
                    case OpCode.NEWSTRUCT:
                        {
                            if (!TryPop(out StackItem x)) return false;
                            switch (x)
                            {
                                // Allow to convert between array and struct
                                case VMArray array:
                                    {
                                        VMArray result;
                                        if (array is Struct)
                                        {
                                            if (instruction.OpCode == OpCode.NEWSTRUCT)
                                            {
                                                result = array;
                                            }
                                            else
                                            {
                                                result = new VMArray();
                                                foreach (StackItem item in array)
                                                    AppendItem(result, item);
                                            }
                                        }
                                        else
                                        {
                                            if (instruction.OpCode == OpCode.NEWARRAY)
                                            {
                                                result = array;
                                            }
                                            else
                                            {
                                                result = new Struct();
                                                foreach (StackItem item in array)
                                                    AppendItem(result, item);
                                            }
                                        }
                                        Push(result);
                                    }
                                    break;
                                case PrimitiveType primitive:
                                    {
                                        int count = (int)primitive.ToBigInteger();
                                        if (count < 0 || !CheckArraySize(count)) return false;
                                        VMArray result = instruction.OpCode == OpCode.NEWARRAY
                                            ? new VMArray()
                                            : new Struct();
                                        for (var i = 0; i < count; i++)
                                            AppendItem(result, StackItem.Null);
                                        Push(result);
                                    }
                                    break;
                                default:
                                    return false;
                            }
                            break;
                        }
                    case OpCode.NEWMAP:
                        {
                            Push(new Map());
                            break;
                        }
                    case OpCode.APPEND:
                        {
                            if (!TryPop(out StackItem newItem)) return false;
                            if (!TryPop(out VMArray array)) return false;
                            if (newItem is Struct s) newItem = CreateClonedStruct(s);
                            if (!AppendItem(array, newItem)) return false;
                            break;
                        }
                    case OpCode.REVERSE:
                        {
                            if (!TryPop(out VMArray array)) return false;
                            array.Reverse();
                            break;
                        }
                    case OpCode.REMOVE:
                        {
                            if (!TryPop(out PrimitiveType key)) return false;
                            if (!TryPop(out StackItem x)) return false;
                            switch (x)
                            {
                                case VMArray array:
                                    int index = (int)key.ToBigInteger();
                                    if (!RemoveItem(array, index)) return false;
                                    break;
                                case Map map:
                                    RemoveItem(map, key);
                                    break;
                                default:
                                    return false;
                            }
                            break;
                        }
                    case OpCode.HASKEY:
                        {
                            if (!TryPop(out PrimitiveType key)) return false;
                            if (!TryPop(out StackItem x)) return false;
                            switch (x)
                            {
                                case VMArray array:
                                    int index = (int)key.ToBigInteger();
                                    if (index < 0) return false;
                                    Push(index < array.Count);
                                    break;
                                case Map map:
                                    Push(map.ContainsKey(key));
                                    break;
                                default:
                                    return false;
                            }
                            break;
                        }
                    case OpCode.KEYS:
                        {
                            if (!TryPop(out Map map)) return false;
                            VMArray array = new VMArray();
                            foreach (PrimitiveType key in map.Keys)
                                AppendItem(array, key);
                            Push(array);
                            break;
                        }
                    case OpCode.VALUES:
                        {
                            ICollection<StackItem> values;
                            if (!TryPop(out StackItem x)) return false;
                            switch (x)
                            {
                                case VMArray array:
                                    values = array;
                                    break;
                                case Map map:
                                    values = map.Values;
                                    break;
                                default:
                                    return false;
                            }
                            VMArray newArray = new VMArray();
                            foreach (StackItem item in values)
                                if (item is Struct s)
                                    AppendItem(newArray, CreateClonedStruct(s));
                                else
                                    AppendItem(newArray, item);
                            Push(newArray);
                            break;
                        }

                    // Exceptions
                    case OpCode.THROW:
                        {
                            return false;
                        }
                    case OpCode.THROWIFNOT:
                        {
                            if (!TryPop(out StackItem x)) return false;
                            if (!x.ToBoolean()) return false;
                            break;
                        }
                    default:
                        return false;
                }
            context.MoveNext();
            return true;
        }

        protected virtual void LoadContext(ExecutionContext context)
        {
            if (InvocationStack.Count >= MaxInvocationStackSize)
                throw new InvalidOperationException();
            InvocationStack.Push(context);
        }

        public ExecutionContext LoadScript(Script script, int rvcount = -1)
        {
            ExecutionContext context = new ExecutionContext(script, CurrentContext?.Script, rvcount);
            LoadContext(context);
            return context;
        }

        protected virtual bool OnSysCall(uint method) => false;

        protected virtual bool PostExecuteInstruction(Instruction instruction)
        {
            CheckZeroReferred();
            return stackitem_count <= MaxStackSize;
        }

        protected virtual bool PreExecuteInstruction() => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(StackItem item)
        {
            TryInsert(0, item);
        }

        public bool RemoveItem(VMArray array, int index)
        {
            if (index < 0 || index >= array.Count) return false;
            RemoveReference(array[index], array);
            array.RemoveAt(index);
            stackitem_count--;
            return true;
        }

        public void RemoveItem(Map map, PrimitiveType key)
        {
            if (!map.Remove(key, out StackItem old_value)) return;
            RemoveReference(old_value, map);
            stackitem_count -= 2;
        }

        private void RemoveReference(StackItem referred, CompoundType parent)
        {
            if (!(referred is CompoundType compound)) return;
            ReferenceTracing tracing = reference_tracing[compound];
            tracing.ObjectReferences[parent] -= 1;
            if (tracing.StackReferences == 0)
                zero_referred.Add(compound);
        }

        public bool SetItem(VMArray array, int index, StackItem item)
        {
            if (index < 0 || index >= array.Count) return false;
            RemoveReference(array[index], array);
            array[index] = item;
            AddReference(item, array);
            return true;
        }

        public bool SetItem(Map map, PrimitiveType key, StackItem value)
        {
            if (map.TryGetValue(key, out StackItem old_value))
            {
                RemoveReference(old_value, map);
            }
            else
            {
                if (map.Count >= MaxArraySize) return false;
                stackitem_count += 2;
            }
            map[key] = value;
            AddReference(value, map);
            return true;
        }

        private bool TryInsert(int index, StackItem item)
        {
            var stack = CurrentContext.EvaluationStack;
            if (index < 0 || index > stack.Count) return false;
            stack.Insert(index, item);
            stackitem_count++;
            if (!(item is CompoundType compound)) return true;
            if (reference_tracing.TryGetValue(compound, out ReferenceTracing tracing))
                tracing.StackReferences++;
            else
                reference_tracing.Add(compound, new ReferenceTracing { StackReferences = 1 });
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPop<T>(out T item) where T : StackItem
        {
            return TryRemove(0, out item);
        }

        private bool TryRemove<T>(int index, out T item) where T : StackItem
        {
            if (!CurrentContext.EvaluationStack.TryRemove(index, out StackItem stackItem))
            {
                item = null;
                return false;
            }
            stackitem_count--;
            item = stackItem as T;
            if (item is null) return false;
            if (!(item is CompoundType item_compound)) return true;
            if (--reference_tracing[item_compound].StackReferences == 0)
                zero_referred.Add(item_compound);
            return true;
        }
    }
}
