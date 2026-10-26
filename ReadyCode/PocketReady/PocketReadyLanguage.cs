// Licensed under the MIT License. See LICENSE in the project root.

using System.IO;
using ReadyCode.Editor;
using ReadyCode.Models;

namespace ReadyCode.PocketReady;

/// <summary>
/// Separate plain UTF-8 dialect. It does not reuse C64 tokenization, renumbering,
/// abbreviation/PETSCII typing or C64 live-memory debugging. Phone diagnostics are authoritative.
/// </summary>
public static class PocketReadyLanguage
{
    public static bool IsSourceFile(string path) => string.Equals(Path.GetExtension(path), ".pbas", StringComparison.OrdinalIgnoreCase);
    public static bool CanUseCommodorePipeline(EditorLanguage language) => language is EditorLanguage.Basic or EditorLanguage.Asm;
    public static IReadOnlyList<string> Keywords { get; } = Array.AsReadOnly((
        "ABS AND ASC ATN CALL CHR$ CLEAR CLOSE CLR COLOR COS DATA DATE$ DEBUG DEF DEG DIM DIRECTORY " +
        "DOUBLE DRAW ELSE END ERROR EXP FILEWRITE FN FOR FRE FUNCTION GET GOSUB GOTO GRAPHICS IF INPUT INT " +
        "KEYCAPTION LEFT$ LEN LET LIST LOAD LOG MID$ MOD NEW NEXT NOT ON OPEN OR ORIENTATION PAUSE PEEK " +
        "PLOT POKE PRINT RAD READ REM RENAME RESTORE RETURN RIGHT$ RLOAD RND RUN SAVE SCREEN SGN SIN SLEEP " +
        "SOFTKEY SOUND SPEED SPRITE SPRITEHIT SQR STATE STEP STOP STR$ SUB SYS TAN THEN TI TI$ TIME TIMER " +
        "TO USE VAL VIEW VOL WAIT WINDOW").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static List<KeywordCompletionData> Completions(string prefix) => Keywords
        .Where(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .OrderBy(value => value, StringComparer.Ordinal)
        .Select(value => new KeywordCompletionData(value, value + " |", "Pocket READY. BASIC keyword; see the device language guide.", "Pocket READY."))
        .ToList();
}
