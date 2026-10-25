// Licensed under the MIT License. See LICENSE in the project root.

using ReadyCode.Models;
using ReadyCode.PocketReady;
using ReadyCode.Tokenizer;
using Xunit;

namespace ReadyCode.Tests;

public sealed class PocketReadyLanguageTests
{
    [Theory]
    [InlineData("example.pbas", EditorLanguage.PocketReady)]
    [InlineData("C:\\work\\EXAMPLE.PBAS", EditorLanguage.PocketReady)]
    [InlineData("example.bas", EditorLanguage.Basic)]
    [InlineData("example.asm", EditorLanguage.Asm)]
    public void DialectSelectionIsExplicitAndPreservesLegacyDefaults(string path, EditorLanguage expected)
    {
        Assert.Equal(expected, LanguageClassifier.Classify(path));
    }

    [Theory]
    [InlineData("example.prg")]
    [InlineData("example.bas")]
    [InlineData("example.pbas")]
    public void PocketSourceNeverUsesBinaryPrgSavePipeline(string path)
    {
        Assert.False(PrgConverter.ShouldTokenizeOnSave(EditorLanguage.PocketReady, path));
        Assert.True(PrgConverter.ShouldTokenizeOnSave(EditorLanguage.Basic, "legacy.prg"));
    }

    [Fact]
    public void ExtendedCompletionsIncludeCallableSdkAndGraphicsFacilities()
    {
        Assert.Contains(PocketReadyLanguage.Completions("fun"), item => item.Text == "FUNCTION");
        Assert.Contains(PocketReadyLanguage.Completions("use"), item => item.Text == "USE");
        Assert.Contains(PocketReadyLanguage.Completions("spri"), item => item.Text == "SPRITE");
        Assert.False(PocketReadyLanguage.CanUseCommodorePipeline(EditorLanguage.PocketReady));
        Assert.True(PocketReadyLanguage.CanUseCommodorePipeline(EditorLanguage.Basic));
        Assert.True(PocketReadyLanguage.CanUseCommodorePipeline(EditorLanguage.Asm));
    }
}
