using System.Text;
using System.Text.RegularExpressions;

namespace EncodingHelper;

// Greatly helped by https://gist.github.com/maxiwheat/91297282b80cd40f10ef066a66b43dee

public static class EncodingResolver
{
    public static Encoding? GuessEncodingFromStream(Stream stream)
    {
        long originalPos = stream.Position;

        byte[] bomBytes = ExtractBOMFromStream(stream);

        //Try to guess encoding with BOM check
        var encodingFound = TryGuessingWithBOMBytes(bomBytes);

        if (encodingFound != null)
        {
            stream.Position = originalPos;
            return encodingFound;
        }

        //We couldn't find encoding with BOM, we have to read all the stream and analyse bytes
        byte[] allBytes = GetAllBytesFromStream(stream, bomBytes);

        stream.Position = originalPos;

        var statsOfBytes = GenerateStatsOfBytes(allBytes);

        //Try to guess if encoding is UTF8 based on all bytes
        if (TryGuessUTF8WithoutBOM(allBytes, statsOfBytes, out var encoding))
        {
            return encoding;
        }

        // At this point, we are almost sure encoding is not UTF8.
        // the two remaining encodings that are most likely are windows-1252 and ISO-8859-1 (latin1)

        if (statsOfBytes.ByteInWindows1252SpecificRange != 0)
        {
            //Bytes in the range 0x80 <-> 0x9F can happen in windows-1252 but not in latin1, since these are the last two probable encodings,
            // we return windows-1252
            return Encoding.GetEncoding(1252);
        }

        // If all bytes are in the range of ISO-8859-1 (latin1), then we return it
        if (statsOfBytes.ByteInLatin1Range == allBytes.Length)
        {
            return Encoding.Latin1;
        }

        //We couldn't guess encoding =(
        return null;
    }

    private static byte[] ExtractBOMFromStream(Stream stream)
    {
        stream.Position = 0;

        //First read only what we need for BOM detection
        byte[] bomBytes = new byte[stream.Length > 4 ? 4 : stream.Length];
        stream.Read(bomBytes, 0, bomBytes.Length);
        return bomBytes;
    }

    private static byte[] GetAllBytesFromStream(Stream stream, byte[] bomBytes)
    {
        byte[] sampleBytes = new byte[stream.Length];

        Array.Copy(bomBytes, sampleBytes, bomBytes.Length);

        if (stream.Length > bomBytes.Length)
        {
            stream.Read(sampleBytes, bomBytes.Length, sampleBytes.Length - bomBytes.Length);
        }

        return sampleBytes;
    }

    private static ByteArrayEncodingStats GenerateStatsOfBytes(byte[] sampleBytes)
    {
        var SequenceOfBytesThatLooksLikeUTF8Count = 0;
        var SequenceOfBytesThatLooksLikeUTF8Total = 0;
        var LikelyUSASCIIBytesInSample = 0;
        var ByteInWindows1252SpecificRange = 0;
        var ByteInLatin1Range = 0;

        // let's analyse all bytes  
        long currentPos = 0;
        int skipUTF8Bytes = 0;
        while (currentPos < sampleBytes.Length)
        {
            //likely US-ASCII characters
            if (IsCommonUSASCIIByte(sampleBytes[currentPos]))
                LikelyUSASCIIBytesInSample++;

            // having a byte in this range is likely a sign of windows-1252 IF we guessed that encoding wasn't UTF8
            if (sampleBytes[currentPos] >= 0x80 && sampleBytes[currentPos] <= 0x9F)
            {
                ByteInWindows1252SpecificRange += 1;
            }

            // having a byte in those range is compatible with latin1
            if (sampleBytes[currentPos] >= 0x20 && sampleBytes[currentPos] <= 0x7E
                || sampleBytes[currentPos] >= 0xA1 && sampleBytes[currentPos] <= 0xFF
                || sampleBytes[currentPos] == 0x0a // \n
                || sampleBytes[currentPos] == 0x09 // \t
                || sampleBytes[currentPos] == 0x0d) // \r
            {
                ByteInLatin1Range += 1;
            }

            //suspicious sequences (look like UTF-8)
            if (skipUTF8Bytes == 0)
            {
                int lengthFound = DetectSequenceLengthOfBytesThatLooksLikeUTF8(sampleBytes, currentPos);

                if (lengthFound > 0)
                {
                    SequenceOfBytesThatLooksLikeUTF8Count++;
                    SequenceOfBytesThatLooksLikeUTF8Total += lengthFound;
                    skipUTF8Bytes = lengthFound - 1;
                }
            }
            else
            {
                skipUTF8Bytes--;
            }

            currentPos++;
        }

        return new ByteArrayEncodingStats
        (
            SequenceOfBytesThatLooksLikeUTF8Count,
            SequenceOfBytesThatLooksLikeUTF8Total,
            LikelyUSASCIIBytesInSample,
            ByteInWindows1252SpecificRange,
            ByteInLatin1Range
        );
    }

    private static Encoding? TryGuessingWithBOMBytes(byte[] BOMBytes)
    {
        if (BOMBytes == null)
            throw new ArgumentNullException(nameof(BOMBytes));

        if (BOMBytes.Length < 2)
            return null;

        // UTF16 LE
        if (BOMBytes[0] == 0xff
            && BOMBytes[1] == 0xfe
            && (BOMBytes.Length < 4
                || BOMBytes[2] != 0
                || BOMBytes[3] != 0
                )
            )
            return Encoding.Unicode;

        // UTF16 BE
        if (BOMBytes[0] == 0xfe
            && BOMBytes[1] == 0xff
            )
            return Encoding.BigEndianUnicode;

        if (BOMBytes.Length < 3)
            return null;

        if (BOMBytes[0] == 0xef && BOMBytes[1] == 0xbb && BOMBytes[2] == 0xbf)
            return Encoding.UTF8;

        if (BOMBytes[0] == 0x2b && BOMBytes[1] == 0x2f && BOMBytes[2] == 0x76)
            return Encoding.UTF7;

        if (BOMBytes.Length < 4)
            return null;

        // UTF32 LE
        if (BOMBytes[0] == 0xff && BOMBytes[1] == 0xfe && BOMBytes[2] == 0 && BOMBytes[3] == 0)
            return Encoding.UTF32;

        // UTF32 BE
        if (BOMBytes[0] == 0 && BOMBytes[1] == 0 && BOMBytes[2] == 0xfe && BOMBytes[3] == 0xff)
            return Encoding.GetEncoding(12001);

        return null;
    }

    private static bool TryGuessUTF8WithoutBOM(byte[] sampleBytes, ByteArrayEncodingStats stats, out Encoding? encodingFound)
    {
        //  Martin Dürst outlines a method for detecting whether something CAN be UTF-8 content 
        //  using regexp, in his w3c.org unicode FAQ entry: 
        //  http://www.w3.org/International/questions/qa-forms-utf-8
        //  adapted here for C#.
        string potentiallyMangledString = Encoding.ASCII.GetString(sampleBytes);
        Regex UTF8Validator = new Regex(@"\A("
            + @"[\x09\x0A\x0D\x20-\x7E]"
            + @"|[\xC2-\xDF][\x80-\xBF]"
            + @"|\xE0[\xA0-\xBF][\x80-\xBF]"
            + @"|[\xE1-\xEC\xEE\xEF][\x80-\xBF]{2}"
            + @"|\xED[\x80-\x9F][\x80-\xBF]"
            + @"|\xF0[\x90-\xBF][\x80-\xBF]{2}"
            + @"|[\xF1-\xF3][\x80-\xBF]{3}"
            + @"|\xF4[\x80-\x8F][\x80-\xBF]{2}"
            + @")*\z");
        if (UTF8Validator.IsMatch(potentiallyMangledString))
        {
            //Unfortunately, just the fact that it CAN be UTF-8 doesn't tell you much about probabilities.
            //If all the characters are in the 0-127 range, no harm done, most western charsets are same as UTF-8 in these ranges.
            //If some of the characters were in the upper range (western accented characters), however, they would likely be mangled to 2-byte by the UTF-8 encoding process.
            // So, we need to play stats.

            // The "Random" likelihood of any pair of randomly generated characters being one 
            //   of these "suspicious" character sequences is:
            //     128 / (256 * 256) = 0.2%.
            //
            // In western text data, that is SIGNIFICANTLY reduced - most text data stays in the <127 
            //   character range, so we assume that more than 1 in 500,000 of these character 
            //   sequences indicates UTF-8. The number 500,000 is completely arbitrary - so sue me.
            //
            // We can only assume these character sequences will be rare if we ALSO assume that this
            //   IS in fact western text - in which case the bulk of the UTF-8 encoded data (that is 
            //   not already suspicious sequences) should be plain US-ASCII bytes. This, I 
            //   arbitrarily decided, should be 80% (a random distribution, eg binary data, would yield 
            //   approx 40%, so the chances of hitting this threshold by accident in random data are 
            //   VERY low).

            if ((stats.SequenceOfBytesThatLooksLikeUTF8Count * 500000.0 / sampleBytes.Length >= 1) //suspicious sequences
                && (
                       //all suspicious, so cannot evaluate proportion of US-Ascii
                       sampleBytes.Length - stats.SequenceOfBytesThatLooksLikeUTF8Total == 0
                       ||
                       stats.LikelyUSASCIIBytesInSample * 1.0 / (sampleBytes.Length - stats.SequenceOfBytesThatLooksLikeUTF8Total) >= 0.8
                   )
                )
            {
                encodingFound = Encoding.UTF8;
                return true;
            }

            //If all caracteres were ascii compatible, we can use UTF8
            if (sampleBytes.Length == stats.LikelyUSASCIIBytesInSample)
            {
                encodingFound = Encoding.UTF8;
                return true;
            }
        }

        encodingFound = null;
        return false;
    }

    private static bool IsCommonUSASCIIByte(byte testByte)
    {
        if (testByte == 0x0A //lf
            || testByte == 0x0D //cr
            || testByte == 0x09 //tab
            || (testByte >= 0x20 && testByte <= 0x2F) //common punctuation
            || (testByte >= 0x30 && testByte <= 0x39) //digits
            || (testByte >= 0x3A && testByte <= 0x40) //common punctuation
            || (testByte >= 0x41 && testByte <= 0x5A) //capital letters
            || (testByte >= 0x5B && testByte <= 0x60) //common punctuation
            || (testByte >= 0x61 && testByte <= 0x7A) //lowercase letters
            || (testByte >= 0x7B && testByte <= 0x7E) //common punctuation
            )
            return true;
        else
            return false;
    }

    private static int DetectSequenceLengthOfBytesThatLooksLikeUTF8(byte[] SampleBytes, long currentPos)
    {
        int lengthFound = 0;

        if (SampleBytes.Length > currentPos + 1
            && SampleBytes[currentPos] == 0xC2
            )
        {
            if (SampleBytes[currentPos + 1] == 0x81
                || SampleBytes[currentPos + 1] == 0x8D
                || SampleBytes[currentPos + 1] == 0x8F
                )
                lengthFound = 2;
            else if (SampleBytes[currentPos + 1] == 0x90
                || SampleBytes[currentPos + 1] == 0x9D
                )
                lengthFound = 2;
            else if (SampleBytes[currentPos + 1] >= 0xA0
                && SampleBytes[currentPos + 1] <= 0xBF
                )
                lengthFound = 2;
        }
        else if (SampleBytes.Length > currentPos + 1
            && SampleBytes[currentPos] == 0xC3
            )
        {
            if (SampleBytes[currentPos + 1] >= 0x80
                && SampleBytes[currentPos + 1] <= 0xBF
                )
                lengthFound = 2;
        }
        else if (SampleBytes.Length > currentPos + 1
            && SampleBytes[currentPos] == 0xC5
            )
        {
            if (SampleBytes[currentPos + 1] == 0x92
                || SampleBytes[currentPos + 1] == 0x93
                )
                lengthFound = 2;
            else if (SampleBytes[currentPos + 1] == 0xA0
                || SampleBytes[currentPos + 1] == 0xA1
                )
                lengthFound = 2;
            else if (SampleBytes[currentPos + 1] == 0xB8
                || SampleBytes[currentPos + 1] == 0xBD
                || SampleBytes[currentPos + 1] == 0xBE
                )
                lengthFound = 2;
        }
        else if (SampleBytes.Length > currentPos + 1
            && SampleBytes[currentPos] == 0xC6
            )
        {
            if (SampleBytes[currentPos + 1] == 0x92)
                lengthFound = 2;
        }
        else if (SampleBytes.Length > currentPos + 1
            && SampleBytes[currentPos] == 0xCB
            )
        {
            if (SampleBytes[currentPos + 1] == 0x86
                || SampleBytes[currentPos + 1] == 0x9C
                )
                lengthFound = 2;
        }
        else if (SampleBytes.Length > currentPos + 2
            && SampleBytes[currentPos] == 0xE2
            )
        {
            if (SampleBytes[currentPos + 1] == 0x80)
            {
                if (SampleBytes[currentPos + 2] == 0x93
                    || SampleBytes[currentPos + 2] == 0x94
                    )
                    lengthFound = 3;
                if (SampleBytes[currentPos + 2] == 0x98
                    || SampleBytes[currentPos + 2] == 0x99
                    || SampleBytes[currentPos + 2] == 0x9A
                    )
                    lengthFound = 3;
                if (SampleBytes[currentPos + 2] == 0x9C
                    || SampleBytes[currentPos + 2] == 0x9D
                    || SampleBytes[currentPos + 2] == 0x9E
                    )
                    lengthFound = 3;
                if (SampleBytes[currentPos + 2] == 0xA0
                    || SampleBytes[currentPos + 2] == 0xA1
                    || SampleBytes[currentPos + 2] == 0xA2
                    )
                    lengthFound = 3;
                if (SampleBytes[currentPos + 2] == 0xA6)
                    lengthFound = 3;
                if (SampleBytes[currentPos + 2] == 0xB0)
                    lengthFound = 3;
                if (SampleBytes[currentPos + 2] == 0xB9
                    || SampleBytes[currentPos + 2] == 0xBA
                    )
                    lengthFound = 3;
            }
            else if (SampleBytes[currentPos + 1] == 0x82
                && SampleBytes[currentPos + 2] == 0xAC
                )
                lengthFound = 3;
            else if (SampleBytes[currentPos + 1] == 0x84
                && SampleBytes[currentPos + 2] == 0xA2
                )
                lengthFound = 3;
        }

        return lengthFound;
    }

    internal record ByteArrayEncodingStats
    (
        long SequenceOfBytesThatLooksLikeUTF8Count,
        long SequenceOfBytesThatLooksLikeUTF8Total,
        long LikelyUSASCIIBytesInSample,
        long ByteInWindows1252SpecificRange,
        long ByteInLatin1Range
    );
}