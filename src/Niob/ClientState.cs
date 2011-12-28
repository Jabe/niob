using System;

namespace Niob
{
    [Flags]
    public enum ClientState
    {
        Unknown = 0,
        Ready = 1,
        Reading = 2,
        Writing = 4,
        KeepingAlive = 8,
        Rendering = 16,
        PostRendering = 32,
        ExpectingContinue = 64,
        PostExpectingContinue = 128,
    }
}