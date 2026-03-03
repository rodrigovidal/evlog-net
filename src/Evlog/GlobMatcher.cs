namespace Evlog;

public static class GlobMatcher
{
    public static bool IsMatch(ReadOnlySpan<char> path, ReadOnlySpan<char> pattern)
    {
        int pi = 0, gi = 0;
        int starPi = -1, starGi = -1;

        while (pi < path.Length)
        {
            if (gi < pattern.Length - 1
                && pattern[gi] == '*' && pattern[gi + 1] == '*')
            {
                starPi = pi;
                starGi = gi;
                gi += 2;
                if (gi < pattern.Length && pattern[gi] == '/')
                    gi++;
            }
            else if (gi < pattern.Length && pattern[gi] == '*')
            {
                starPi = pi;
                starGi = gi;
                gi++;
            }
            else if (gi < pattern.Length && pattern[gi] == path[pi])
            {
                pi++;
                gi++;
            }
            else if (starGi >= 0)
            {
                bool isDoubleStar = starGi < pattern.Length - 1
                    && pattern[starGi] == '*' && pattern[starGi + 1] == '*';

                if (!isDoubleStar && path[starPi] == '/')
                    return false;

                starPi++;
                pi = starPi;
                gi = starGi;

                if (isDoubleStar)
                {
                    gi += 2;
                    if (gi < pattern.Length && pattern[gi] == '/')
                        gi++;
                }
                else
                {
                    gi++;
                }
            }
            else
            {
                return false;
            }
        }

        while (gi < pattern.Length)
        {
            if (gi < pattern.Length - 1 && pattern[gi] == '*' && pattern[gi + 1] == '*')
                gi += 2;
            else if (pattern[gi] == '*')
                gi++;
            else if (pattern[gi] == '/')
                gi++;
            else
                break;
        }

        return gi == pattern.Length;
    }
}
