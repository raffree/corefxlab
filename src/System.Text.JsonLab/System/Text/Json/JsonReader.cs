﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Buffers.Reader;
using System.Collections.Generic;
using System.Diagnostics;

namespace System.Text.JsonLab
{
    public ref struct Utf8JsonReader
    {
        // We are using a ulong to represent our nested state, so we can only go 64 levels deep.
        internal const int StackFreeMaxDepth = sizeof(ulong) * 8;

        private readonly ReadOnlySpan<byte> _buffer;

        public int Index { get; private set; }

        public int StartLocation { get; private set; }

        public int MaxDepth
        {
            get
            {
                return _maxDepth;
            }
            set
            {
                if (value <= 0)
                    JsonThrowHelper.ThrowArgumentException("Max depth must be positive.");
                _maxDepth = value;
                if (_maxDepth > StackFreeMaxDepth)
                    _stack = new Stack<bool>();
            }
        }

        private int _maxDepth;

        private BufferReader<byte> _reader;

        private Stack<bool> _stack;

        // Depth tracks the recursive depth of the nested objects / arrays within the JSON data.
        public int Depth { get; private set; }

        // This mask represents a tiny stack to track the state during nested transitions.
        // The first bit represents the state of the current level (1 == object, 0 == array).
        // Each subsequent bit is the parent / containing type (object or array). Since this
        // reader does a linear scan, we only need to keep a single path as we go through the data.
        private ulong _containerMask;

        // These properties are helpers for determining the current state of the reader
        internal bool InArray => !_inObject;
        private bool _inObject;

        /// <summary>
        /// Gets the token type of the last processed token in the JSON stream.
        /// </summary>
        public JsonTokenType TokenType { get; private set; }

        /// <summary>
        /// Gets the value as a ReadOnlySpan<byte> of the last processed token. The contents of this
        /// is only relevant when <see cref="TokenType" /> is <see cref="JsonTokenType.Value" /> or
        /// <see cref="JsonTokenType.PropertyName" />. Otherwise, this value should be set to
        /// <see cref="ReadOnlySpan{T}.Empty"/>.
        /// </summary>
        public ReadOnlySpan<byte> Value { get; private set; }

        /// <summary>
        /// Gets the JSON value type of the last processed token. The contents of this
        /// is only relevant when <see cref="TokenType" /> is <see cref="JsonTokenType.Value" /> or
        /// <see cref="JsonTokenType.PropertyName" />.
        /// </summary>
        public JsonValueType ValueType { get; private set; }

        private readonly bool _isSingleSegment;

        /// <summary>
        /// Constructs a new JsonReader instance. This is a stack-only type.
        /// </summary>
        /// <param name="data">The <see cref="Span{byte}"/> value to consume. </param>
        /// <param name="encoder">An encoder used for decoding bytes from <paramref name="data"/> into characters.</param>
        public Utf8JsonReader(ReadOnlySpan<byte> data)
        {
            _reader = default;
            _isSingleSegment = true;
            _buffer = data;
            Depth = 0;
            _containerMask = 0;
            Index = 0;
            StartLocation = Index;
            _stack = null;
            _maxDepth = StackFreeMaxDepth;

            TokenType = JsonTokenType.None;
            Value = ReadOnlySpan<byte>.Empty;
            ValueType = JsonValueType.Unknown;
            _inObject = false;
        }

        public Utf8JsonReader(in ReadOnlySequence<byte> data)
        {
            _reader = new BufferReader<byte>(data);
            _isSingleSegment = data.IsSingleSegment; //true;
            _buffer = _reader.CurrentSpan;  //data.ToArray();
            Depth = 0;
            _containerMask = 0;
            Index = 0;
            StartLocation = Index;
            _stack = null;
            _maxDepth = StackFreeMaxDepth;

            TokenType = JsonTokenType.None;
            Value = ReadOnlySpan<byte>.Empty;
            ValueType = JsonValueType.Unknown;
            _inObject = false;
        }

        /// <summary>
        /// Read the next token from the data buffer.
        /// </summary>
        /// <returns>True if the token was read successfully, else false.</returns>
        public bool Read()
        {
            return _isSingleSegment ? ReadSingleSegment() : ReadMultiSegment(ref _reader);
        }

        private void SkipWhiteSpace(ref BufferReader<byte> reader)
        {
            Index += (int)reader.SkipPastAny(JsonConstants.Space, JsonConstants.CarriageReturn, JsonConstants.LineFeed, JsonConstants.Tab);
        }

        private bool ReadFirstToken(ref BufferReader<byte> reader, byte first)
        {
            if (first == JsonConstants.OpenBrace)
            {
                Depth++;
                _containerMask = 1;
                TokenType = JsonTokenType.StartObject;
                reader.Advance(1);
                Index++;
                _inObject = true;
            }
            else if (first == JsonConstants.OpenBracket)
            {
                Depth++;
                TokenType = JsonTokenType.StartArray;
                reader.Advance(1);
                Index++;
            }
            else
            {
                ConsumeSingleValue(ref reader, first);
            }
            return true;
        }

        private bool ReadMultiSegment(ref BufferReader<byte> reader)
        {
            bool retVal = false;

            if (!reader.TryPeek(out byte first))
                goto Done;

            if (first <= JsonConstants.Space)
            {
                SkipWhiteSpace(ref reader);
                if (!reader.TryPeek(out first))
                    goto Done;
            }

            StartLocation = Index;

            if (TokenType == JsonTokenType.None)
                goto ReadFirstToken;

            if (TokenType == JsonTokenType.StartObject)
            {
                reader.Advance(1);
                Index++;
                if (first == JsonConstants.CloseBrace)
                    EndObject();
                else
                {
                    if (first != JsonConstants.Quote) JsonThrowHelper.ThrowJsonReaderException();
                    StartLocation++;
                    ConsumePropertyNameUtf8MultiSegment(ref reader);
                }
            }
            else if (TokenType == JsonTokenType.StartArray)
            {
                if (first == JsonConstants.CloseBracket)
                {
                    reader.Advance(1);
                    Index++;
                    EndArray();
                }
                else
                    ConsumeValueUtf8MultiSegment(ref reader, first);
            }
            else if (TokenType == JsonTokenType.PropertyName)
            {
                ConsumeValueUtf8MultiSegment(ref reader, first);
            }
            else
            {
                ConsumeNextUtf8MultiSegment(ref reader, first);
            }

            retVal = true;

        Done:
            return retVal;

        ReadFirstToken:
            retVal = ReadFirstToken(ref reader, first);
            goto Done;
        }

        private bool ReadFirstToken(byte first)
        {
            if (first == JsonConstants.OpenBrace)
            {
                Depth++;
                _containerMask = 1;
                TokenType = JsonTokenType.StartObject;
                Index++;
                _inObject = true;
            }
            else if (first == JsonConstants.OpenBracket)
            {
                Depth++;
                TokenType = JsonTokenType.StartArray;
                Index++;
            }
            else
            {
                ConsumeSingleValue(first);
            }
            return true;
        }

        internal bool NoMoreData => Index >= (uint)_buffer.Length;

        private bool ReadSingleSegment()
        {
            bool retVal = false;

            if (Index >= (uint)_buffer.Length)
                goto Done;

            byte first = _buffer[Index];

            if (first <= JsonConstants.Space)
            {
                SkipWhiteSpaceUtf8();
                if (Index >= (uint)_buffer.Length)
                    goto Done;
                first = _buffer[Index];
            }

            StartLocation = Index;

            if (TokenType == JsonTokenType.None)
            {
                goto ReadFirstToken;
            }

            if (TokenType == JsonTokenType.StartObject)
            {
                Index++;
                if (first == JsonConstants.CloseBrace)
                    EndObject();
                else
                {
                    if (first != JsonConstants.Quote) JsonThrowHelper.ThrowJsonReaderException();
                    StartLocation++;
                    ConsumePropertyNameUtf8();
                }
            }
            else if (TokenType == JsonTokenType.StartArray)
            {
                if (first == JsonConstants.CloseBracket)
                {
                    Index++;
                    EndArray();
                }
                else
                    ConsumeValueUtf8(first);
            }
            else if (TokenType == JsonTokenType.PropertyName)
            {
                ConsumeValueUtf8(first);
            }
            else
            {
                ConsumeNextUtf8(first);
            }

            retVal = true;

        Done:
            return retVal;

        ReadFirstToken:
            retVal = ReadFirstToken(first);
            goto Done;
        }

        public void Skip()
        {
            if (TokenType == JsonTokenType.PropertyName)
            {
                Read();
            }

            if (TokenType == JsonTokenType.StartObject || TokenType == JsonTokenType.StartArray)
            {
                int depth = Depth;
                while (Read() && depth < Depth)
                {
                }
            }
        }

        private void StartObject()
        {
            if (Depth >= MaxDepth)
                JsonThrowHelper.ThrowJsonReaderException();

            Depth++;

            if (Depth <= StackFreeMaxDepth)
                _containerMask = (_containerMask << 1) | 1;
            else
                _stack.Push(true);

            TokenType = JsonTokenType.StartObject;
            _inObject = true;
        }

        private void EndObject()
        {
            if (!_inObject || Depth <= 0)
                JsonThrowHelper.ThrowJsonReaderException();

            if (Depth <= StackFreeMaxDepth)
            {
                _containerMask >>= 1;
                _inObject = (_containerMask & 1) != 0;
            }
            else
            {
                _inObject = _stack.Pop();
            }

            Depth--;
            TokenType = JsonTokenType.EndObject;
        }

        private void StartArray()
        {
            if (Depth >= MaxDepth)
                JsonThrowHelper.ThrowJsonReaderException();

            Depth++;

            if (Depth <= StackFreeMaxDepth)
                _containerMask = _containerMask << 1;
            else
                _stack.Push(false);

            TokenType = JsonTokenType.StartArray;
            _inObject = false;
        }

        private void EndArray()
        {
            if (_inObject || Depth <= 0)
                JsonThrowHelper.ThrowJsonReaderException();

            if (Depth <= StackFreeMaxDepth)
            {
                _containerMask >>= 1;
                _inObject = (_containerMask & 1) != 0;
            }
            else
            {
                _inObject = _stack.Pop();
            }

            Depth--;
            TokenType = JsonTokenType.EndArray;
        }

        private void ConsumeNextUtf8MultiSegment(ref BufferReader<byte> reader, byte marker)
        {
            reader.Advance(1);
            Index++;
            if (marker == JsonConstants.ListSeperator)
            {
                if (!reader.TryPeek(out byte first))
                    JsonThrowHelper.ThrowJsonReaderException();

                if (first <= JsonConstants.Space)
                {
                    SkipWhiteSpace(ref reader);
                    // The next character must be a start of a property name or value.
                    if (!reader.TryPeek(out first))
                        JsonThrowHelper.ThrowJsonReaderException();
                }

                StartLocation = Index;
                if (_inObject)
                {
                    if (first != JsonConstants.Quote)
                        JsonThrowHelper.ThrowJsonReaderException();

                    reader.Advance(1);
                    Index++;
                    StartLocation++;
                    ConsumePropertyNameUtf8MultiSegment(ref reader);
                }
                else
                {
                    ConsumeValueUtf8MultiSegment(ref reader, first);
                }
            }
            else if (marker == JsonConstants.CloseBrace)
            {
                EndObject();
            }
            else if (marker == JsonConstants.CloseBracket)
            {
                EndArray();
            }
            else
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }
        }

        /// <summary>
        /// This method consumes the next token regardless of whether we are inside an object or an array.
        /// For an object, it reads the next property name token. For an array, it just reads the next value.
        /// </summary>
        private void ConsumeNextUtf8(byte marker)
        {
            Index++;
            if (marker == JsonConstants.ListSeperator)
            {
                if (Index >= (uint)_buffer.Length)
                    JsonThrowHelper.ThrowJsonReaderException();

                byte first = _buffer[Index];

                if (first <= JsonConstants.Space)
                {
                    SkipWhiteSpaceUtf8();
                    // The next character must be a start of a property name or value.
                    if (Index >= (uint)_buffer.Length)
                        JsonThrowHelper.ThrowJsonReaderException();
                    first = _buffer[Index];
                }

                StartLocation = Index;
                if (_inObject)
                {
                    if (first != JsonConstants.Quote)
                        JsonThrowHelper.ThrowJsonReaderException();

                    Index++;
                    StartLocation++;
                    ConsumePropertyNameUtf8();
                }
                else
                {
                    ConsumeValueUtf8(first);
                }
            }
            else if (marker == JsonConstants.CloseBrace)
            {
                EndObject();
            }
            else if (marker == JsonConstants.CloseBracket)
            {
                EndArray();
            }
            else
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }
        }

        private void ConsumeValueUtf8MultiSegment(ref BufferReader<byte> reader, byte marker)
        {
            TokenType = JsonTokenType.Value;

            if (marker == JsonConstants.Quote)
            {
                reader.Advance(1);
                Index++;
                StartLocation++;
                ConsumeStringUtf8MultiSegment(ref reader);
            }
            else if (marker == JsonConstants.OpenBrace)
            {
                reader.Advance(1);
                Index++;
                StartObject();
                ValueType = JsonValueType.Object;
            }
            else if (marker == JsonConstants.OpenBracket)
            {
                reader.Advance(1);
                Index++;
                StartArray();
                ValueType = JsonValueType.Array;
            }
            else if (marker - '0' <= '9' - '0')
            {
                ConsumeNumberUtf8MultiSegment(ref reader);
            }
            else if (marker == '-')
            {
                //TODO: Is this a valid check or do we need to do an Advance to be sure?
                if (reader.End) JsonThrowHelper.ThrowJsonReaderException();
                ConsumeNumberUtf8MultiSegment(ref reader);
            }
            else if (marker == 'f')
            {
                ConsumeFalseUtf8MultiSegment(ref reader);
            }
            else if (marker == 't')
            {
                ConsumeTrueUtf8MultiSegment(ref reader);
            }
            else if (marker == 'n')
            {
                ConsumeNullUtf8MultiSegment(ref reader);
            }
            else if (marker == '/')
            {
                // TODO: Comments?
                JsonThrowHelper.ThrowNotImplementedException();
            }
            else
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }
        }

        /// <summary>
        /// This method contains the logic for processing the next value token and determining
        /// what type of data it is.
        /// </summary>
        private void ConsumeValueUtf8(byte marker)
        {
            TokenType = JsonTokenType.Value;

            if (marker == JsonConstants.Quote)
            {
                Index++;
                StartLocation++;
                ConsumeStringUtf8();
            }
            else if (marker == JsonConstants.OpenBrace)
            {
                Index++;
                StartObject();
                ValueType = JsonValueType.Object;
            }
            else if (marker == JsonConstants.OpenBracket)
            {
                Index++;
                StartArray();
                ValueType = JsonValueType.Array;
            }
            else if (marker - '0' <= '9' - '0')
            {
                ConsumeNumberUtf8();
            }
            else if (marker == '-')
            {
                if (_buffer.Length - 2 > Index) JsonThrowHelper.ThrowJsonReaderException();
                ConsumeNumberUtf8();
            }
            else if (marker == 'f')
            {
                ConsumeFalseUtf8();
            }
            else if (marker == 't')
            {
                ConsumeTrueUtf8();
            }
            else if (marker == 'n')
            {
                ConsumeNullUtf8();
            }
            else if (marker == '/')
            {
                // TODO: Comments?
                JsonThrowHelper.ThrowNotImplementedException();
            }
            else
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }
        }

        private void ConsumeSingleValue(ref BufferReader<byte> reader, byte marker)
        {
            TokenType = JsonTokenType.Value;

            if (marker == JsonConstants.Quote)
            {
                reader.Advance(1);
                Index++;
                StartLocation++;
                ConsumeStringUtf8MultiSegment(ref reader);
            }
            else if (marker - '0' <= '9' - '0')
            {
                //TODO: Validate number
                ReadOnlySequence<byte> sequence = reader.Sequence.Slice(reader.Position);
                Value = sequence.IsSingleSegment ? sequence.First.Span : sequence.ToArray();
                ValueType = JsonValueType.Number;
                int length = reader.UnreadSpan.Length;
                reader.Advance(length);
                Index += length;
            }
            else if (marker == '-')
            {
                //TODO: Is this a valid check?
                if (reader.End) JsonThrowHelper.ThrowJsonReaderException();
                ReadOnlySequence<byte> sequence = reader.Sequence.Slice(reader.Position);
                Value = sequence.IsSingleSegment ? sequence.First.Span : sequence.ToArray();
                ValueType = JsonValueType.Number;
                int length = reader.UnreadSpan.Length;
                reader.Advance(length);
                Index += length;
            }
            else if (marker == 'f')
            {
                ConsumeFalseUtf8MultiSegment(ref reader);
            }
            else if (marker == 't')
            {
                ConsumeTrueUtf8MultiSegment(ref reader);
            }
            else if (marker == 'n')
            {
                ConsumeNullUtf8MultiSegment(ref reader);
            }
            else if (marker == '/')
            {
                // TODO: Comments?
                JsonThrowHelper.ThrowNotImplementedException();
            }
            else
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }
        }

        private void ConsumeSingleValue(byte marker)
        {
            TokenType = JsonTokenType.Value;

            if (marker == JsonConstants.Quote)
            {
                Index++;
                StartLocation++;
                ConsumeStringUtf8();
            }
            else if (marker - '0' <= '9' - '0')
            {
                //TODO: Validate number
                Value = _buffer;
                ValueType = JsonValueType.Number;
                Index += _buffer.Length;
            }
            else if (marker == '-')
            {
                if (_buffer.Length < 2) JsonThrowHelper.ThrowJsonReaderException();
                Value = _buffer;
                ValueType = JsonValueType.Number;
                Index += _buffer.Length;
            }
            else if (marker == 'f')
            {
                ConsumeFalseUtf8();
            }
            else if (marker == 't')
            {
                ConsumeTrueUtf8();
            }
            else if (marker == 'n')
            {
                ConsumeNullUtf8();
            }
            else if (marker == '/')
            {
                // TODO: Comments?
                JsonThrowHelper.ThrowNotImplementedException();
            }
            else
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }
        }

        private void ConsumeNumberUtf8MultiSegment(ref BufferReader<byte> reader)
        {
            //TODO: Increment Index by number of bytes advanced
            if (!reader.TryReadToAny(out ReadOnlySpan<byte> span, JsonConstants.Delimiters, advancePastDelimiter: false))
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }

            Value = span;
            ValueType = JsonValueType.Number;
        }

        private void ConsumeNumberUtf8()
        {
            int idx = _buffer.Slice(Index).IndexOfAny(JsonConstants.Delimiters);
            if (idx == -1)
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }

            Value = _buffer.Slice(Index, idx);
            ValueType = JsonValueType.Number;
            Index += idx;
        }

        private void ConsumeNullUtf8MultiSegment(ref BufferReader<byte> reader)
        {
            Value = JsonConstants.NullValue;
            ValueType = JsonValueType.Null;

            if (!reader.IsNext(JsonConstants.NullValue, advancePast: true))
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }
            Index += 4;
        }

        private void ConsumeNullUtf8()
        {
            Value = JsonConstants.NullValue;
            ValueType = JsonValueType.Null;

            if (!_buffer.Slice(Index).StartsWith(Value))
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }
            Index += 4;
        }

        private void ConsumeFalseUtf8MultiSegment(ref BufferReader<byte> reader)
        {
            Value = JsonConstants.FalseValue;
            ValueType = JsonValueType.False;

            if (!reader.IsNext(JsonConstants.FalseValue, advancePast: true))
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }
            Index += 5;
        }

        private void ConsumeFalseUtf8()
        {
            Value = JsonConstants.FalseValue;
            ValueType = JsonValueType.False;

            if (!_buffer.Slice(Index).StartsWith(Value))
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }
            Index += 5;
        }

        private void ConsumeTrueUtf8MultiSegment(ref BufferReader<byte> reader)
        {
            Value = JsonConstants.TrueValue;
            ValueType = JsonValueType.True;

            if (!reader.IsNext(JsonConstants.TrueValue, advancePast: true))
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }
            Index += 4;
        }

        private void ConsumeTrueUtf8()
        {
            Value = JsonConstants.TrueValue;
            ValueType = JsonValueType.True;

            if (!_buffer.Slice(Index).StartsWith(Value))
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }
            Index += 4;
        }

        private void ConsumePropertyNameUtf8MultiSegment(ref BufferReader<byte> reader)
        {
            ConsumeStringUtf8MultiSegment(ref reader);

            if (!reader.TryPeek(out byte first))
                JsonThrowHelper.ThrowJsonReaderException();

            if (first <= JsonConstants.Space)
            {
                SkipWhiteSpace(ref reader);
                if (!reader.TryPeek(out first))
                    JsonThrowHelper.ThrowJsonReaderException();
            }

            // The next character must be a key / value seperator. Validate and skip.
            if (first != JsonConstants.KeyValueSeperator)
                JsonThrowHelper.ThrowJsonReaderException();

            TokenType = JsonTokenType.PropertyName;
            reader.Advance(1);
            Index++;
        }

        private void ConsumePropertyNameUtf8()
        {
            ConsumeStringUtf8();

            //Create local copy to avoid bounds checks.
            ReadOnlySpan<byte> localCopy = _buffer;
            if (Index >= (uint)localCopy.Length)
                JsonThrowHelper.ThrowJsonReaderException();

            byte first = localCopy[Index];

            if (first <= JsonConstants.Space)
            {
                SkipWhiteSpaceUtf8();
                if (Index >= (uint)localCopy.Length)
                    JsonThrowHelper.ThrowJsonReaderException();
                first = localCopy[Index];
            }

            // The next character must be a key / value seperator. Validate and skip.
            if (first != JsonConstants.KeyValueSeperator)
            {
                JsonThrowHelper.ThrowJsonReaderException();
            }

            TokenType = JsonTokenType.PropertyName;
            Index++;
        }

        public bool TryReadUntil(out ReadOnlySpan<byte> span, byte delimiter)
        {
            ReadOnlySpan<byte> remaining = _reader.UnreadSpan;

            //TODO: Optimize looking for nested quotes
            int i = 0;
            while (true)
            {
                int counter = 0;
                i += remaining.Slice(i).IndexOf(delimiter);
                if (i == -1)
                    break;
                if (i == 0)
                {
                    goto Done;
                }
                for (int j = i - 1; j >= 0; j--)
                {
                    if (remaining[j] != JsonConstants.ReverseSolidus)
                    {
                        if (counter % 2 == 0)
                            goto Done;
                        break;
                    }
                    else
                        counter++;
                }
                i++;
            }
            return TryReadUntilSlow(out span, delimiter, remaining.Length);
        Done:
            span = remaining.Slice(0, i);
            _reader.Advance(i + 1);
            return true;
        }

        private bool TryReadUntilSlow(out ReadOnlySpan<byte> span, byte delimiter, int skip)
        {
            BufferReader<byte> copy = _reader;
            if (skip > 0)
                _reader.Advance(skip);
            ReadOnlySpan<byte> remaining = _reader.UnreadSpan;

            while (!_reader.End)
            {
                int counter = 0;
                int index = remaining.IndexOf(delimiter);
                if (index != -1)
                {
                    // Found the delimiter. Move to it, slice, then move past it.
                    if (index > 0)
                    {
                        for (int j = index - 1; j >= 0; j--)
                        {
                            if (remaining[j] != JsonConstants.ReverseSolidus)
                            {
                                if (counter % 2 == 0)
                                {
                                    _reader.Advance(index);
                                    goto Done;
                                }
                                goto KeepLooking;
                            }
                            else
                                counter++;
                        }
                    }

                Done:
                    ReadOnlySequence<byte> sequence = _reader.Sequence.Slice(copy.Position, _reader.Position);
                    _reader.Advance(1);
                    span = sequence.IsSingleSegment ? sequence.First.Span : sequence.ToArray();
                    return true;
                }
            KeepLooking:
                _reader.Advance(remaining.Length);
                remaining = _reader.CurrentSpan;
            }

            // Didn't find anything, reset our original state.
            _reader = copy;
            span = default;
            return false;
        }

        private void ConsumeStringWithNestedQuotes(ref BufferReader<byte> reader)
        {
            //TODO: Optimize looking for nested quotes
            //TODO: Avoid redoing first IndexOf searches

            BufferReader<byte> copy = reader;
            ReadOnlySpan<byte> buffer = reader.UnreadSpan;
            if (0 >= (uint)buffer.Length)
            {
                reader.Advance(buffer.Length);
                Index += buffer.Length;
                buffer = reader.CurrentSpan;
            }

            while (!reader.End)
            {
                int counter = 1;
                int index = buffer.IndexOf(JsonConstants.Quote);
                if (index != -1)
                {
                    // Found the delimiter. Move to it, slice, then move past it.
                    if (index == 0 || buffer[index - 1] != JsonConstants.ReverseSolidus)
                        goto Done;

                    for (int j = index - 2; j >= 0; j--)
                    {
                        if (buffer[j] != JsonConstants.ReverseSolidus)
                        {
                            if (counter % 2 == 0)
                                goto Done;
                            break;
                        }
                        else
                            counter++;
                    }
                    goto KeepLooking;

                Done:
                    reader.Advance(index);
                    Index += index;
                    ReadOnlySequence<byte> sequence = reader.Sequence.Slice(copy.Position, reader.Position);
                    reader.Advance(1);
                    Index++;
                    Value = sequence.IsSingleSegment ? sequence.First.Span : sequence.ToArray();
                    ValueType = JsonValueType.String;
                    return;
                }
            KeepLooking:
                reader.Advance(index + 1);
                buffer = reader.UnreadSpan;
            }

            JsonThrowHelper.ThrowJsonReaderException();
        }

        private void ConsumeStringUtf8MultiSegmentSlow(ref BufferReader<byte> reader)
        {
            BufferReader<byte> copy = reader;
            ReadOnlySpan<byte> buffer = reader.UnreadSpan;
            if (0 >= (uint)buffer.Length)
            {
                reader.Advance(buffer.Length);
                Index += buffer.Length;
                buffer = reader.CurrentSpan;
            }

            while (!reader.End)
            {
                int index = buffer.IndexOf(JsonConstants.Quote);
                if (index != -1)
                {
                    // Found the delimiter. Move to it, slice, then move past it.
                    if (index == 0 || buffer[index - 1] != JsonConstants.ReverseSolidus)
                    {
                        reader.Advance(index);
                        Index += index;
                        ReadOnlySequence<byte> sequence = reader.Sequence.Slice(copy.Position, reader.Position);
                        reader.Advance(1);
                        Index++;
                        Value = sequence.IsSingleSegment ? sequence.First.Span : sequence.ToArray();
                        ValueType = JsonValueType.String;
                        return;
                    }
                    reader = copy;
                    ConsumeStringWithNestedQuotes(ref reader);
                }
                reader.Advance(buffer.Length);
                buffer = reader.CurrentSpan;
            }

            JsonThrowHelper.ThrowJsonReaderException();
        }

        private void ConsumeStringUtf8MultiSegment(ref BufferReader<byte> reader)
        {
            ReadOnlySpan<byte> buffer = reader.UnreadSpan;
            int idx = buffer.IndexOf(JsonConstants.Quote);
            if (idx < 0)
            {
                ConsumeStringUtf8MultiSegmentSlow(ref reader);
                return;
            }

            if (idx == 0 || buffer[idx - 1] != JsonConstants.ReverseSolidus)
            {
                Value = buffer.Slice(0, idx);
                ValueType = JsonValueType.String;

                idx++;
                reader.Advance(idx);
                Index += idx;
                return;
            }
            ConsumeStringWithNestedQuotes(ref reader);
        }

        private void ConsumeStringUtf8()
        {
            //Create local copy to avoid bounds checks.
            ReadOnlySpan<byte> localCopy = _buffer;

            int idx = localCopy.Slice(Index).IndexOf(JsonConstants.Quote);
            if (idx < 0)
                JsonThrowHelper.ThrowJsonReaderException();

            Debug.Assert(Index >= 1);

            if (localCopy[idx + Index - 1] != JsonConstants.ReverseSolidus)
            {
                Value = localCopy.Slice(Index, idx);
                ValueType = JsonValueType.String;
                Index += idx + 1;
            }
            else
            {
                ConsumeStringWithNestedQuotes();
            }
        }

        private void ConsumeStringWithNestedQuotes()
        {
            //TODO: Optimize looking for nested quotes
            //TODO: Avoid redoing first IndexOf search
            int i = Index;
            while (true)
            {
                int counter = 0;
                int foundIdx = _buffer.Slice(i).IndexOf(JsonConstants.Quote);
                if (foundIdx == -1)
                    JsonThrowHelper.ThrowJsonReaderException();
                if (foundIdx == 0)
                    break;
                for (int j = i + foundIdx - 1; j >= i; j--)
                {
                    if (_buffer[j] != JsonConstants.ReverseSolidus)
                    {
                        if (counter % 2 == 0)
                        {
                            i += foundIdx;
                            goto Done;
                        }
                        break;
                    }
                    else
                        counter++;
                }
                i += foundIdx + 1;
            }

        Done:
            Value = _buffer.Slice(Index, i - Index);
            ValueType = JsonValueType.String;

            i++;
            Index = i;
        }

        private void SkipWhiteSpaceUtf8()
        {
            //Create local copy to avoid bounds checks.
            ReadOnlySpan<byte> localCopy = _buffer;
            for (; Index < localCopy.Length; Index++)
            {
                byte val = localCopy[Index];
                if (val != JsonConstants.Space &&
                    val != JsonConstants.CarriageReturn &&
                    val != JsonConstants.LineFeed &&
                    val != JsonConstants.Tab)
                {
                    break;
                }
            }
        }
    }
}
