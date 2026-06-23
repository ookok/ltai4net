// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  InitCommandHandler — interactive setup wizard (DeerFlow-inspired)
// ═══════════════════════════════════════════════════════════════

using LTAI.Cli.Commands;

namespace LTAI.Cli;

partial class Program
{
    private static async Task<int> HandleInit()
    {
        return await InitCommand.RunAsync().ConfigureAwait(false);
    }
}
