using NUnit.Framework;
using System.Collections.Generic;
using System.IO;

public sealed class AuthoringTooltipTests
{
    [Test]
    public void SerializedFieldsHaveJapaneseTooltips()
    {
        var missingTooltips = new List<string>();
        var files = Directory.GetFiles("Assets/Scripts", "*.cs", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);

            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                if (!lines[lineIndex].Contains("[SerializeField]"))
                {
                    continue;
                }

                if (SerializedFieldHasTooltip(lines, lineIndex))
                {
                    continue;
                }

                missingTooltips.Add($"{file}:{lineIndex + 1}");
            }
        }

        Assert.IsEmpty(missingTooltips, string.Join("\n", missingTooltips));
    }

    private static bool SerializedFieldHasTooltip(string[] lines, int serializeFieldLineIndex)
    {
        var startIndex = serializeFieldLineIndex;

        while (startIndex > 0 && IsAttributeLine(lines[startIndex - 1]))
        {
            startIndex--;
        }

        var endIndex = serializeFieldLineIndex;

        while (endIndex + 1 < lines.Length && IsAttributeLine(lines[endIndex + 1]))
        {
            endIndex++;
        }

        for (var lineIndex = startIndex; lineIndex <= endIndex; lineIndex++)
        {
            if (lines[lineIndex].Contains("[Tooltip(\""))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAttributeLine(string line)
    {
        return line.TrimStart().StartsWith("[");
    }
}
