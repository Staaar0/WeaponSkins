using System.Text;

namespace WeaponSkinsBot.Discord;

public static class CommandLine
{
    public static List<string> Split(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        char quote = '\0';

        foreach (var c in input)
        {
            if ((c == '"' || c == '\'') && (!quoted || c == quote))
            {
                if (quoted)
                {
                    quoted = false;
                    quote = '\0';
                }
                else
                {
                    quoted = true;
                    quote = c;
                }
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }
}
