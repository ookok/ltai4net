using LTAI.Core.Configuration;
using Microsoft.Extensions.Options;

namespace LTAI.Core.System;

public sealed class DataPathResolver(IOptions<LTAIOptions> options)
{
    public string GetPath(string subpath)
    {
        var dataDir = options.Value.DataDirectory ?? ".livingtree";
        return Path.Combine(AppContext.BaseDirectory, dataDir, subpath);
    }

    public string DataDirectory => GetPath("");
}
