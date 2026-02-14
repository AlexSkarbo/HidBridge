namespace HidControlServer.Services;

// FLV tag parsing utilities and AVC config extraction for WS-FLV.
/// <summary>
/// Parses FLV tags and extracts AVC config.
/// </summary>
internal static class FlvParserService
{
    /// <summary>
    /// Tries to build avc config tag from keyframe.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <param name="configTag">The configTag.</param>
    /// <returns>Result.</returns>
    internal static bool TryBuildAvcConfigTagFromKeyframe(byte[] tag, out byte[] configTag)
    {
        configTag = Array.Empty<byte>();
        // Convert a keyframe tag into an AVC sequence header when missing.
        if (tag.Length < 16 || tag[0] != 9)
        {
            return false;
        }
        int dataSize = (tag[1] << 16) | (tag[2] << 8) | tag[3];
        if (dataSize < 5 || tag.Length < 11 + dataSize + 4)
        {
            return false;
        }
        int dataStart = 11;
        byte frameAndCodec = tag[dataStart];
        int codecId = frameAndCodec & 0x0F;
        if (codecId != 7)
        {
            return false;
        }
        byte avcPacketType = tag[dataStart + 1];
        if (avcPacketType != 1)
        {
            return false;
        }
        int payloadStart = dataStart + 5;
        int payloadLen = dataSize - 5;
        if (payloadLen <= 0 || payloadStart + payloadLen > tag.Length)
        {
            return false;
        }

        var payload = new ReadOnlySpan<byte>(tag, payloadStart, payloadLen);
        if (!TryExtractSpsPps(payload, out var sps, out var pps, out int nalLenSize))
        {
            return false;
        }

        byte profile = sps.Length > 1 ? sps[1] : (byte)0x64;
        byte compat = sps.Length > 2 ? sps[2] : (byte)0x00;
        byte level = sps.Length > 3 ? sps[3] : (byte)0x1F;
        int lenSizeMinusOne = Math.Clamp(nalLenSize - 1, 0, 3);

        // Build AVCDecoderConfigurationRecord (avcC).
        int avccLen = 11 + sps.Length + pps.Length;
        int outDataSize = 5 + avccLen;
        int total = 11 + outDataSize + 4;
        var outTag = new byte[total];

        // Tag header
        outTag[0] = 9;
        outTag[1] = (byte)((outDataSize >> 16) & 0xFF);
        outTag[2] = (byte)((outDataSize >> 8) & 0xFF);
        outTag[3] = (byte)(outDataSize & 0xFF);
        // Copy timestamp (3 bytes + ext)
        outTag[4] = tag[4];
        outTag[5] = tag[5];
        outTag[6] = tag[6];
        outTag[7] = tag[7];
        // stream id = 0
        outTag[8] = 0;
        outTag[9] = 0;
        outTag[10] = 0;

        int pos = 11;
        // Video tag header
        outTag[pos++] = (byte)((1 << 4) | 7); // keyframe + AVC
        outTag[pos++] = 0; // AVC sequence header
        outTag[pos++] = 0;
        outTag[pos++] = 0;
        outTag[pos++] = 0; // composition time

        // AVCDecoderConfigurationRecord
        outTag[pos++] = 1; // configurationVersion
        outTag[pos++] = profile;
        outTag[pos++] = compat;
        outTag[pos++] = level;
        outTag[pos++] = (byte)(0xFC | lenSizeMinusOne);
        outTag[pos++] = 0xE1; // one SPS
        outTag[pos++] = (byte)((sps.Length >> 8) & 0xFF);
        outTag[pos++] = (byte)(sps.Length & 0xFF);
        sps.CopyTo(outTag.AsSpan(pos));
        pos += sps.Length;
        outTag[pos++] = 1; // one PPS
        outTag[pos++] = (byte)((pps.Length >> 8) & 0xFF);
        outTag[pos++] = (byte)(pps.Length & 0xFF);
        pps.CopyTo(outTag.AsSpan(pos));
        pos += pps.Length;

        int prevTagSize = outDataSize + 11;
        outTag[pos++] = (byte)((prevTagSize >> 24) & 0xFF);
        outTag[pos++] = (byte)((prevTagSize >> 16) & 0xFF);
        outTag[pos++] = (byte)((prevTagSize >> 8) & 0xFF);
        outTag[pos++] = (byte)(prevTagSize & 0xFF);

        configTag = outTag;
        return true;
    }

    /// <summary>
    /// Tries to extract sps pps.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <param name="sps">The sps.</param>
    /// <param name="pps">The pps.</param>
    /// <param name="nalLenSize">The nalLenSize.</param>
    /// <returns>Result.</returns>
    private static bool TryExtractSpsPps(ReadOnlySpan<byte> payload, out byte[] sps, out byte[] pps, out int nalLenSize)
    {
        sps = Array.Empty<byte>();
        pps = Array.Empty<byte>();
        nalLenSize = 4;

        if (payload.Length >= 4 && payload[0] == 0 && payload[1] == 0 && (payload[2] == 1 || (payload[2] == 0 && payload[3] == 1)))
        {
            return TryExtractAnnexB(payload, out sps, out pps);
        }

        foreach (int lenSize in new[] { 4, 2, 1 })
        {
            if (TryExtractLengthPrefixed(payload, lenSize, out sps, out pps))
            {
                nalLenSize = lenSize;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Tries to extract length prefixed.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <param name="lenSize">The lenSize.</param>
    /// <param name="sps">The sps.</param>
    /// <param name="pps">The pps.</param>
    /// <returns>Result.</returns>
    private static bool TryExtractLengthPrefixed(ReadOnlySpan<byte> payload, int lenSize, out byte[] sps, out byte[] pps)
    {
        sps = Array.Empty<byte>();
        pps = Array.Empty<byte>();
        int pos = 0;
        while (pos + lenSize <= payload.Length)
        {
            int naluLen = 0;
            for (int i = 0; i < lenSize; i++)
            {
                naluLen = (naluLen << 8) | payload[pos + i];
            }
            pos += lenSize;
            if (naluLen <= 0 || pos + naluLen > payload.Length)
            {
                return false;
            }
            var nalu = payload.Slice(pos, naluLen);
            int nalType = nalu[0] & 0x1F;
            if (nalType == 7 && sps.Length == 0)
            {
                sps = nalu.ToArray();
            }
            else if (nalType == 8 && pps.Length == 0)
            {
                pps = nalu.ToArray();
            }
            pos += naluLen;
        }
        return sps.Length > 0 && pps.Length > 0;
    }

    /// <summary>
    /// Tries to extract annex b.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <param name="sps">The sps.</param>
    /// <param name="pps">The pps.</param>
    /// <returns>Result.</returns>
    private static bool TryExtractAnnexB(ReadOnlySpan<byte> payload, out byte[] sps, out byte[] pps)
    {
        sps = Array.Empty<byte>();
        pps = Array.Empty<byte>();
        int i = 0;
        while (i < payload.Length - 3)
        {
            int start = FindStartCode(payload, i);
            if (start < 0)
            {
                break;
            }
            int next = FindStartCode(payload, start + 3);
            int naluStart = start;
            while (naluStart < payload.Length && payload[naluStart] == 0)
            {
                naluStart++;
            }
            if (naluStart < payload.Length && payload[naluStart] == 1)
            {
                naluStart++;
            }
            int naluEnd = next > 0 ? next : payload.Length;
            if (naluEnd <= naluStart)
            {
                i = start + 3;
                continue;
            }
            var nalu = payload.Slice(naluStart, naluEnd - naluStart);
            int nalType = nalu[0] & 0x1F;
            if (nalType == 7 && sps.Length == 0)
            {
                sps = nalu.ToArray();
            }
            else if (nalType == 8 && pps.Length == 0)
            {
                pps = nalu.ToArray();
            }
            i = naluEnd;
        }
        return sps.Length > 0 && pps.Length > 0;
    }

    /// <summary>
    /// Executes FindStartCode.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <param name="from">The from.</param>
    /// <returns>Result.</returns>
    private static int FindStartCode(ReadOnlySpan<byte> payload, int from)
    {
        for (int i = from; i < payload.Length - 3; i++)
        {
            if (payload[i] == 0 && payload[i + 1] == 0)
            {
                if (payload[i + 2] == 1)
                {
                    return i;
                }
                if (i + 3 < payload.Length && payload[i + 2] == 0 && payload[i + 3] == 1)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    /// <summary>
    /// TagReadResult.
    /// </summary>
    internal enum TagReadResult
    {
        NeedMore,
        Read,
        Skip,
        Invalid
    }

    /// <summary>
    /// Parses FLV tags and extracts AVC config.
    /// </summary>
    internal sealed class ByteBuffer
    {
        private byte[] _buf;
        private int _start;
        private int _end;
        private bool _headerRead;
        private int _avcNalLenSize = 4;
        private readonly int _maxCapacity;
        private const int MaxTagSize = 8 * 1024 * 1024;
        private int _debugVideoTagsRemaining = 30;
        private int _debugIdrRemaining = 20;
        private int _debugPayloadRemaining = 8;

        /// <summary>
        /// Executes ByteBuffer.
        /// </summary>
        /// <param name="capacity">The capacity.</param>
        /// <param name="maxCapacity">The maxCapacity.</param>
        public ByteBuffer(int capacity, int maxCapacity)
        {
            _maxCapacity = Math.Max(capacity, maxCapacity);
            _buf = new byte[capacity];
        }

        public bool HasHeader => _headerRead;

        /// <summary>
        /// Executes Append.
        /// </summary>
        /// <param name="data">The data.</param>
        public void Append(ReadOnlySpan<byte> data)
        {
            if (data.Length > _maxCapacity)
            {
                data = data.Slice(data.Length - _maxCapacity);
                _start = 0;
                _end = 0;
                _headerRead = false;
            }
            else if (Length + data.Length > _maxCapacity)
            {
                _start = 0;
                _end = 0;
                _headerRead = false;
            }
            EnsureCapacity(data.Length);
            data.CopyTo(_buf.AsSpan(_end));
            _end += data.Length;
        }

        /// <summary>
        /// Tries to read header.
        /// </summary>
        /// <param name="header">The header.</param>
        /// <returns>Result.</returns>
        public bool TryReadHeader(out byte[] header)
        {
            header = Array.Empty<byte>();
            if (_headerRead)
            {
                return false;
            }
            if (Length < 13)
            {
                return false;
            }
            var span = new ReadOnlySpan<byte>(_buf, _start, 13);
            if (span[0] != (byte)'F' || span[1] != (byte)'L' || span[2] != (byte)'V')
            {
                _start++;
                return false;
            }
            header = ReadBytes(13);
            _headerRead = true;
            return true;
        }

        public TagReadResult TryReadTag(
            out byte[] tag,
            out bool isMeta,
            out bool isKeyframe,
            out bool isVideoConfig,
            out int timestampMs,
            out byte tagType)
        {
            tag = Array.Empty<byte>();
            isMeta = false;
            isKeyframe = false;
            isVideoConfig = false;
            timestampMs = 0;
            tagType = 0;
            if (Length < 11)
            {
                return TagReadResult.NeedMore;
            }
            var span = new ReadOnlySpan<byte>(_buf, _start, 11);
            int dataSize = (span[1] << 16) | (span[2] << 8) | span[3];
            if (dataSize <= 0 || dataSize > MaxTagSize)
            {
                Resync();
                return TagReadResult.Invalid;
            }
            if (span[8] != 0 || span[9] != 0 || span[10] != 0)
            {
                _start++;
                return TagReadResult.Invalid;
            }
            int totalSize = 11 + dataSize + 4;
            if (Length < totalSize)
            {
                return TagReadResult.NeedMore;
            }
            tagType = span[0];
            if (tagType != 8 && tagType != 9 && tagType != 18)
            {
                Resync();
                return TagReadResult.Invalid;
            }
            timestampMs =
                (span[7] << 24) |
                (span[4] << 16) |
                (span[5] << 8) |
                span[6];
            bool shouldConvertAnnexB = false;
            bool promoteKeyframe = false;
            if (tagType == 18)
            {
                isMeta = true;
            }
            else if (tagType == 9 && dataSize > 1)
            {
                int dataStart = _start + 11;
                int dataEnd = _start + 11 + dataSize;
                byte frameAndCodec = _buf[dataStart];
                int frameType = (frameAndCodec >> 4) & 0x0F;
                int codecId = frameAndCodec & 0x0F;
                if (codecId != 7)
                {
                    ReadBytes(totalSize);
                    return TagReadResult.Skip;
                }
                if (dataSize < 5)
                {
                    Resync();
                    return TagReadResult.Invalid;
                }
                if (dataSize > 2)
                {
                    byte avcPacketType = _buf[dataStart + 1];
                    if (avcPacketType == 0)
                    {
                        if (dataSize < 12)
                        {
                            Resync();
                            return TagReadResult.Invalid;
                        }
                        int cfg = dataStart + 5;
                        if (cfg + 5 <= dataEnd)
                        {
                            int lenSize = (_buf[cfg + 4] & 0x03) + 1;
                            if (lenSize >= 1 && lenSize <= 4)
                            {
                                _avcNalLenSize = lenSize;
                            }
                        }
                        isVideoConfig = true;
                    }
                    else if (avcPacketType == 1)
                    {
                        isKeyframe = false;
                        if (dataSize > 5)
                        {
                            int payloadStart = dataStart + 5;
                            int payloadLen = dataSize - 5;
                            if (payloadLen > 0 && payloadStart + payloadLen <= _end)
                            {
                                var payload = new ReadOnlySpan<byte>(_buf, payloadStart, payloadLen);
                                bool hasIdr = ContainsIdrPayload(payload, _avcNalLenSize);
                                if (hasIdr)
                                {
                                    isKeyframe = true;
                                    promoteKeyframe = frameType != 1;
                                    if (_debugIdrRemaining-- > 0)
                                    {
                                        ServerEventLog.Log(
                                            "flv",
                                            "keyframe_detected",
                                            new { timestampMs, nalLen = _avcNalLenSize, payloadLen });
                                    }
                                }
                                else if (frameType == 1 && _debugIdrRemaining-- > 0)
                                {
                                    ServerEventLog.Log(
                                        "flv",
                                        "keyframe_no_idr",
                                        new { timestampMs, nalLen = _avcNalLenSize, payloadLen });
                                }
                                if (LooksLikeAnnexB(payload))
                                {
                                    shouldConvertAnnexB = true;
                                }
                                if (_debugPayloadRemaining-- > 0)
                                {
                                    int headLen = Math.Min(12, payload.Length);
                                    var head = payload.Slice(0, headLen).ToArray();
                                    ServerEventLog.Log(
                                        "flv",
                                        "payload_head",
                                        new
                                        {
                                            timestampMs,
                                            nalLen = _avcNalLenSize,
                                            headLen,
                                            looksAnnexB = LooksLikeAnnexB(payload),
                                            headHex = BitConverter.ToString(head)
                                        });
                                }
                            }
                        }
                    }
                    if (_debugVideoTagsRemaining > 0)
                    {
                        _debugVideoTagsRemaining--;
                        ServerEventLog.Log(
                            "flv",
                            "tag_probe",
                            new
                            {
                                frameType,
                                avcPacketType,
                                isKeyframe,
                                avcNalLenSize = _avcNalLenSize,
                                dataSize
                            });
                    }
                }
                else
                {
                    isKeyframe = frameType == 1;
                }
            }
            tag = ReadBytes(totalSize);
            if (shouldConvertAnnexB && tagType == 9 && dataSize > 5)
            {
                if (TryConvertAnnexBPayload(tag, dataSize, _avcNalLenSize, out var converted))
                {
                    tag = converted;
                }
            }
            if (promoteKeyframe && tagType == 9 && tag.Length > 11)
            {
                tag[11] = (byte)((tag[11] & 0x0F) | 0x10);
            }
            return TagReadResult.Read;
        }

        /// <summary>
        /// Converts Annex B payloads to length-prefixed format inside an FLV tag.
        /// </summary>
        private static bool TryConvertAnnexBPayload(byte[] tag, int dataSize, int nalLenSize, out byte[] converted)
        {
            converted = Array.Empty<byte>();
            int dataStart = 11;
            if (dataSize < 5 || tag.Length < dataStart + dataSize + 4)
            {
                return false;
            }
            int payloadStart = dataStart + 5;
            int payloadLen = dataSize - 5;
            if (payloadLen <= 0 || payloadStart + payloadLen > tag.Length)
            {
                return false;
            }
            var payload = new ReadOnlySpan<byte>(tag, payloadStart, payloadLen);
            if (!LooksLikeAnnexB(payload))
            {
                return false;
            }
            if (!TryConvertAnnexBToLengthPrefixed(payload, nalLenSize, out var newPayload))
            {
                return false;
            }
            int newDataSize = dataSize - payloadLen + newPayload.Length;
            int total = 11 + newDataSize + 4;
            var outTag = new byte[total];
            Buffer.BlockCopy(tag, 0, outTag, 0, 11);
            outTag[1] = (byte)((newDataSize >> 16) & 0xFF);
            outTag[2] = (byte)((newDataSize >> 8) & 0xFF);
            outTag[3] = (byte)(newDataSize & 0xFF);
            Buffer.BlockCopy(tag, dataStart, outTag, dataStart, 5);
            Buffer.BlockCopy(newPayload, 0, outTag, payloadStart, newPayload.Length);
            int prevTagSize = newDataSize + 11;
            int prevPos = 11 + newDataSize;
            outTag[prevPos] = (byte)((prevTagSize >> 24) & 0xFF);
            outTag[prevPos + 1] = (byte)((prevTagSize >> 16) & 0xFF);
            outTag[prevPos + 2] = (byte)((prevTagSize >> 8) & 0xFF);
            outTag[prevPos + 3] = (byte)(prevTagSize & 0xFF);
            converted = outTag;
            return true;
        }

        /// <summary>
        /// Checks IDR in payload (length-prefixed or Annex B).
        /// </summary>
        private static bool ContainsIdrPayload(ReadOnlySpan<byte> payload, int nalLenSize)
        {
            if (LooksLikeAnnexB(payload))
            {
                return ContainsIdrAnnexB(payload);
            }
            if (ContainsIdrLengthPrefixed(payload, nalLenSize))
            {
                return true;
            }
            if (nalLenSize != 4 && ContainsIdrLengthPrefixed(payload, 4))
            {
                return true;
            }
            if (nalLenSize != 2 && ContainsIdrLengthPrefixed(payload, 2))
            {
                return true;
            }
            if (nalLenSize != 1 && ContainsIdrLengthPrefixed(payload, 1))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Converts Annex B payload to length-prefixed NAL units.
        /// </summary>
        private static bool TryConvertAnnexBToLengthPrefixed(ReadOnlySpan<byte> payload, int nalLenSize, out byte[] converted)
        {
            converted = Array.Empty<byte>();
            nalLenSize = Math.Clamp(nalLenSize, 1, 4);
            int total = 0;
            int pos = 0;
            while (true)
            {
                int start = FindStartCode(payload, pos);
                if (start < 0)
                {
                    break;
                }
                int startCodeLen = payload[start + 2] == 1 ? 3 : 4;
                int naluStart = start + startCodeLen;
                int next = FindStartCode(payload, naluStart);
                int naluEnd = next >= 0 ? next : payload.Length;
                if (naluEnd > naluStart)
                {
                    int naluLen = naluEnd - naluStart;
                    total += nalLenSize + naluLen;
                }
                pos = naluEnd;
            }
            if (total <= 0)
            {
                return false;
            }
            var output = new byte[total];
            int outPos = 0;
            pos = 0;
            while (true)
            {
                int start = FindStartCode(payload, pos);
                if (start < 0)
                {
                    break;
                }
                int startCodeLen = payload[start + 2] == 1 ? 3 : 4;
                int naluStart = start + startCodeLen;
                int next = FindStartCode(payload, naluStart);
                int naluEnd = next >= 0 ? next : payload.Length;
                if (naluEnd > naluStart)
                {
                    int naluLen = naluEnd - naluStart;
                    int len = naluLen;
                    for (int i = nalLenSize - 1; i >= 0; i--)
                    {
                        output[outPos + i] = (byte)(len & 0xFF);
                        len >>= 8;
                    }
                    outPos += nalLenSize;
                    payload.Slice(naluStart, naluLen).CopyTo(output.AsSpan(outPos));
                    outPos += naluLen;
                }
                pos = naluEnd;
            }
            converted = output;
            return outPos == total;
        }

        /// <summary>
        /// Checks IDR in length-prefixed payload.
        /// </summary>
        private static bool ContainsIdrLengthPrefixed(ReadOnlySpan<byte> payload, int nalLenSize)
        {
            nalLenSize = Math.Clamp(nalLenSize, 1, 4);
            int pos = 0;
            while (pos + nalLenSize <= payload.Length)
            {
                int naluLen = 0;
                for (int i = 0; i < nalLenSize; i++)
                {
                    naluLen = (naluLen << 8) | payload[pos + i];
                }
                pos += nalLenSize;
                if (naluLen <= 0 || pos + naluLen > payload.Length)
                {
                    return false;
                }
                int nalType = payload[pos] & 0x1F;
                if (nalType == 5)
                {
                    return true;
                }
                pos += naluLen;
            }
            return false;
        }

        /// <summary>
        /// Checks IDR in Annex B payload.
        /// </summary>
        private static bool ContainsIdrAnnexB(ReadOnlySpan<byte> payload)
        {
            int pos = 0;
            while (pos < payload.Length - 3)
            {
                int start = FindStartCode(payload, pos);
                if (start < 0)
                {
                    break;
                }
                int startCodeLen = payload[start + 2] == 1 ? 3 : 4;
                int naluStart = start + startCodeLen;
                int next = FindStartCode(payload, naluStart);
                int naluEnd = next >= 0 ? next : payload.Length;
                if (naluEnd > naluStart)
                {
                    int nalType = payload[naluStart] & 0x1F;
                    if (nalType == 5)
                    {
                        return true;
                    }
                }
                pos = naluEnd;
            }
            return false;
        }

        /// <summary>
        /// Checks if payload looks like Annex B (start code near beginning).
        /// </summary>
        private static bool LooksLikeAnnexB(ReadOnlySpan<byte> payload)
        {
            int start = FindStartCode(payload, 0, maxOffset: 4);
            return start >= 0;
        }

        /// <summary>
        /// Finds Annex B start code.
        /// </summary>
        private static int FindStartCode(ReadOnlySpan<byte> payload, int from, int maxOffset = int.MaxValue)
        {
            int limit = payload.Length - 3;
            int max = maxOffset == int.MaxValue
                ? limit
                : Math.Min(limit, from + Math.Max(0, maxOffset));
            for (int i = from; i <= max; i++)
            {
                if (payload[i] == 0 && payload[i + 1] == 0)
                {
                    if (payload[i + 2] == 1)
                    {
                        return i;
                    }
                    if (i + 3 < payload.Length && payload[i + 2] == 0 && payload[i + 3] == 1)
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        private int Length => _end - _start;

        /// <summary>
        /// Reads bytes.
        /// </summary>
        /// <param name="count">The count.</param>
        /// <returns>Result.</returns>
        private byte[] ReadBytes(int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(_buf, _start, result, 0, count);
            _start += count;
            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }
            return result;
        }

        /// <summary>
        /// Ensures capacity.
        /// </summary>
        /// <param name="extra">The extra.</param>
        private void EnsureCapacity(int extra)
        {
            int needed = _end + extra;
            if (needed <= _buf.Length)
            {
                return;
            }
            int length = Length;
            int newSize = Math.Min(_maxCapacity, Math.Max(_buf.Length * 2, needed));
            var next = new byte[newSize];
            Buffer.BlockCopy(_buf, _start, next, 0, length);
            _buf = next;
            _start = 0;
            _end = length;
        }

        /// <summary>
        /// Executes Resync.
        /// </summary>
        private void Resync()
        {
            int idx = FindNextTagStart();
            if (idx >= 0)
            {
                _start = idx;
                return;
            }
            _start = Math.Max(_start + 1, _end - 11);
        }

        /// <summary>
        /// Executes FindNextTagStart.
        /// </summary>
        /// <returns>Result.</returns>
        private int FindNextTagStart()
        {
            int limit = _end - 11;
            for (int i = _start + 1; i <= limit; i++)
            {
                byte t = _buf[i];
                if (t != 8 && t != 9 && t != 18)
                {
                    continue;
                }
                if (_buf[i + 8] != 0 || _buf[i + 9] != 0 || _buf[i + 10] != 0)
                {
                    continue;
                }
                int size = (_buf[i + 1] << 16) | (_buf[i + 2] << 8) | _buf[i + 3];
                if (size <= 0 || size > MaxTagSize)
                {
                    continue;
                }
                int total = 11 + size + 4;
                if (i + total > _end)
                {
                    continue;
                }
                int prev = (_buf[i + total - 4] << 24) | (_buf[i + total - 3] << 16) | (_buf[i + total - 2] << 8) | _buf[i + total - 1];
                if (prev != size + 11)
                {
                    continue;
                }
                return i;
            }
            return -1;
        }

    }
}
