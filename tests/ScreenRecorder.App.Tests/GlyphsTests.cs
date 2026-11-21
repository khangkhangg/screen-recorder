using System.Reflection;
using ScreenRecorder.App;

namespace ScreenRecorder.App.Tests;

public class GlyphsTests
{
    [Fact]
    public void Every_glyph_is_one_private_use_character()
    {
        var glyphs = typeof(Glyphs).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).ToList();
        Assert.NotEmpty(glyphs);
        foreach (var f in glyphs)
        {
            var s = (string)f.GetRawConstantValue()!;
            Assert.True(s.Length == 1 && s[0] >= '' && s[0] <= '', $"{f.Name} is \"{s}\"");
        }
    }
}
