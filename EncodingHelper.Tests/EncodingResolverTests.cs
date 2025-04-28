using System.Text;
using FluentAssertions;
using Xunit;

namespace EncodingHelper.Tests;

internal class EncodingResolverData : TheoryData<string, Encoding>
{
    public EncodingResolverData()
    {
        // This line allow us to use Encoding.GetEncoding(1252) and get windows-1252 encoding
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // The encoding in the filname is the encoding that I used when I created the file.
        // In reality, we have no constant explicite information on what encoding was used to encode the file. The goal is to find an encoding that will decode
        // exactly the same characters the user used.
        //
        // For example : file 'ISO-8859-1_with_ascii_chars_only.csv' was encoded with ISO-8859-1 (is also called latin1)
        // but since we have ascii compatible bytes only, we can use UTF8 to decode the file (ASCII is retrocompatible with UTF8)
        // It is the same logic for the others tests.

        Add("ISO-8859-1_with_ascii_chars_only.csv", Encoding.UTF8);
        Add("ISO-8859-1_with_chars_that_arent_ascii.csv", Encoding.Latin1);
        Add("UTF8_with_ascii_chars_only.csv", Encoding.UTF8);
        Add("UTF8_with_ascii_chars_only_with_BOM.csv", Encoding.UTF8);
        Add("UTF8_with_ascii_chars_only_without_BOM.csv", Encoding.UTF8);
        Add("UTF8_with_chars_that_arent_ascii_without_BOM.csv", Encoding.UTF8);
        Add("UTF16-BE_with_ascii_chars_only.csv", Encoding.BigEndianUnicode);
        Add("UTF16-BE_with_chars_that_arent_ascii.csv", Encoding.BigEndianUnicode);
        Add("UTF16-LE_with_ascii_chars_only.csv", Encoding.Unicode);
        Add("UTF16-LE_with_chars_that_arent_ascii.csv", Encoding.Unicode);
        Add("WINDOWS_1252_with_ascii_chars_only.csv", Encoding.UTF8);
        Add("WINDOWS_1252_with_chars_that_arent_ascii.csv", Encoding.Latin1);
        Add("WINDOWS_1252_with_specific_chars.csv", Encoding.GetEncoding(1252));
    }
}

public sealed class EncodingResolverTests
{
    [Theory]
    [ClassData(typeof(EncodingResolverData))]
    public void Parse_CSV_File_Theory(string fileName, Encoding encoding)
    {
        // Arrange
        var directoryTestFilesName = "Files";
        var testFilePath = string.Join('/', directoryTestFilesName, fileName);

        using var stream = File.OpenRead(testFilePath);

        // Act
        var result = EncodingResolver.GuessEncodingFromStream(stream);

        // Assert

        if (result is null)
        {
            Assert.Fail($"{nameof(result)} shouldn't be null");
        }

        result!.EncodingName.Should().Be(encoding?.EncodingName);
    }
}