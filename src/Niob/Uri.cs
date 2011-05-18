using System;
using System.Text;

namespace Niob
{
    public static class Uri
    {
        public static string Encode(string input)
        {
            string s = input;

            for (int i = s.Length - 1; i >= 0; i--)
            {
                char c = s[i];

                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' ||
                    c == '.' || c == '~')
                {
                    continue;
                }

                if (c == '!' || c == '*' || c == '\'' || c == '(' || c == ')' || c == ';' || c == ':' || c == '@' ||
                    c == '&' || c == '=' || c == '+' || c == '$' || c == ',' || c == '/' || c == '?' || c == '#' ||
                    c == '(' || c == ')')
                {
                    s = s.Replace(c.ToString(), string.Format("%{0:X}", (int) c));
                    continue;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(c.ToString());

                s = s.Insert(i + 1, BytesToPercent(bytes));
                s = s.Remove(i, 1);
            }

            return s;
        }

        private static string BytesToPercent(byte[] value)
        {
            var target = new char[value.Length*3];

            for (int i = 0, j = 0; i < value.Length; i++, j += 3)
            {
                byte b = value[i];

                target[j] = '%';
                target[j + 1] = ToHex(b/16);
                target[j + 2] = ToHex(b%16);
            }

            return new string(target);
        }

        private static char ToHex(int value)
        {
            if (value < 0x0 || value > 0xF) throw new ArgumentOutOfRangeException("value");

            if (value < 10)
            {
                return (char) (value + '0');
            }

            return (char) (value + 'A' - 10);
        }
    }
}