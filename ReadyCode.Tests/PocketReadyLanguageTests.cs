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

public sealed class PocketReadySourceFileTests
{
    [Fact]
    public async Task SourceSaveReplacesCompleteUtf8WithoutBomOrStagingResidue()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "readycode-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            string path = System.IO.Path.Combine(directory, "source.pbas");
            await System.IO.File.WriteAllTextAsync(path, "ORIGINAL");
            const string source = "START:PRINT \"£\"\r\nREM Keep case";
            await PocketReadySourceFile.SaveAsync(path, source);
            Assert.Equal(new System.Text.UTF8Encoding(false).GetBytes(source), await System.IO.File.ReadAllBytesAsync(path));
            Assert.Single(System.IO.Directory.GetFiles(directory));
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CancelledSavePreservesExistingSource()
    {
        string path = System.IO.Path.GetTempFileName();
        try
        {
            await System.IO.File.WriteAllTextAsync(path, "ORIGINAL");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PocketReadySourceFile.SaveAsync(path, "REPLACEMENT", cancellation.Token));
            Assert.Equal("ORIGINAL", await System.IO.File.ReadAllTextAsync(path));
        }
        finally { System.IO.File.Delete(path); }
    }
}
