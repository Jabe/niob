using System;

namespace Niob
{
    public static class TimeSpanExtensions
    {
        public static string ToStringAuto(this TimeSpan span)
        {
            string result;

            long ticks = span.Ticks;
            string sign = (Math.Sign(ticks) == -1) ? "-" : "";

            ticks = Math.Abs(ticks);

            if (ticks >= TimeSpan.TicksPerMinute)
                result = span.ToString(@"hh\:mm\:ss");
            else if (ticks >= TimeSpan.TicksPerSecond*10)
                result = span.TotalSeconds.ToString("f1") + " s";
            else if (ticks >= TimeSpan.TicksPerMillisecond)
                result = span.TotalMilliseconds.ToString("f0") + " ms";
            else
                result = (ticks/10.0).ToString("f0") + " µs";

            return sign + result;
        }
    }
}