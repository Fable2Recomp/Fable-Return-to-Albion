using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Gdb;

public static class Engine_GdbSaveMap
{
    private const string EntityOpen = "<Entity name=\"";

    private const string EntityClose = "</Entity>";

    public static List<KeyValuePair<uint, string>> Parse(byte[]? saveBytes)
    {
        var entries = new List<KeyValuePair<uint, string>>();
        if (saveBytes is null || saveBytes.Length == 0)
        {
            return entries;
        }

        string xml = Encoding.UTF8.GetString(saveBytes);
        int cursor = 0;

        while (true)
        {
            int open = xml.IndexOf(EntityOpen, cursor, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            int nameStart = open + EntityOpen.Length;
            int nameEnd = xml.IndexOf('"', nameStart);
            if (nameEnd < 0)
            {
                break;
            }

            string name = xml[nameStart..nameEnd];

            int hashStart = xml.IndexOf("0x", nameEnd, StringComparison.Ordinal);
            if (hashStart < 0)
            {
                break;
            }

            int hashEnd = xml.IndexOf('<', hashStart);
            if (hashEnd < 0)
            {
                break;
            }

            int close = xml.IndexOf(EntityClose, hashEnd, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            uint hash = 0;
            for (int i = hashStart + 2; i < hashEnd; i++)
            {
                char c = xml[i];
                int digit;
                if (c >= '0' && c <= '9')
                {
                    digit = c - '0';
                }
                else if (c >= 'A' && c <= 'F')
                {
                    digit = c - 'A' + 10;
                }
                else if (c >= 'a' && c <= 'f')
                {
                    digit = c - 'a' + 10;
                }
                else
                {
                    continue;
                }

                hash = unchecked((hash << 4) | (uint)digit);
            }

            entries.Add(new KeyValuePair<uint, string>(hash, name));
            cursor = close + EntityClose.Length;
        }

        return entries;
    }
}
