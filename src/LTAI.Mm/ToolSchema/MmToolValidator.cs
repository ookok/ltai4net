using System.Reflection;
using LTAI.Mm.Core;
using LTAI.Mm.Ir;

namespace LTAI.Mm.ToolSchema;

public static class MmToolValidator
{
    public static string? ValidateInput(string toolName, ParameterInfo[] parameters, object?[] args)
    {
        for (int i = 0; i < Math.Min(parameters.Length, args.Length); i++)
        {
            var mmAttr = parameters[i].GetCustomAttribute<MMAttribute>(false);
            if (mmAttr == null || mmAttr.IsExcluded) continue;

            var tag = mmAttr.Parsed;
            var value = args[i];

            var result = Validator.Validate(value, tag);
            if (!result.IsValid)
            {
                return $"Tool '{toolName}' parameter '{parameters[i].Name}': {result.Error}";
            }
        }
        return null;
    }

    public static Dictionary<string, string?> ValidateAll(string toolName, Dictionary<string, object?> namedArgs,
        ParameterInfo[] parameters)
    {
        var errors = new Dictionary<string, string?>();
        foreach (var param in parameters)
        {
            var mmAttr = param.GetCustomAttribute<MMAttribute>(false);
            if (mmAttr == null || mmAttr.IsExcluded) continue;

            if (param.Name != null && namedArgs.TryGetValue(param.Name, out var value))
            {
                var tag = mmAttr.Parsed;
                var result = Validator.Validate(value, tag);
                if (!result.IsValid)
                    errors[param.Name] = result.Error;
            }
        }
        return errors;
    }
}
