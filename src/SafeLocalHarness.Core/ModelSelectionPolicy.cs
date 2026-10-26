namespace SafeLocalHarness.Core;

public static class ModelSelectionPolicy
{
    public static string? ResolveForPlanning(string requested, IEnumerable<string> installed)
    {
        var models = installed.Where(IsLocalCoderModel).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var compatible = FindCompatible(requested, models);
        if (compatible is not null) return compatible;

        // Resource adaptation is a safety boundary: never silently replace a
        // recommended small model with a larger installed model. A smaller
        // compatible coder may be used, otherwise setup must install the
        // recommended model or the caller must remain in tool-only mode.
        var requestedSize = ParameterSize(requested);
        if (requestedSize <= 0) return null;
        // A smaller model is only a stand-in within the same family, so its behaviour stays comparable.
        var requestedFamily = Family(requested);
        return models
            .Where(model => Family(model).Equals(requestedFamily, StringComparison.OrdinalIgnoreCase))
            .Where(model => ParameterSize(model) is > 0 and var size && size <= requestedSize)
            .OrderByDescending(ParameterSize)
            .FirstOrDefault();
    }

    private static string Family(string model)
    {
        var colon = model.IndexOf(':');
        return colon <= 0 ? model : model[..colon];
    }

    public static string? FindCompatible(string requested, IEnumerable<string> installed)
    {
        var exact = installed.FirstOrDefault(model => model.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var requestedFamily = FamilyAndSize(requested);
        return requestedFamily is null
            ? null
            : installed.FirstOrDefault(model => FamilyAndSize(model)?.Equals(requestedFamily, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool IsLocalCoderModel(string model) =>
        model.StartsWith("qwen2.5-coder:", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("qwen3.5:", StringComparison.OrdinalIgnoreCase);

    private static string? FamilyAndSize(string model)
    {
        var colon = model.IndexOf(':');
        if (colon <= 0 || colon == model.Length - 1) return null;
        var tag = model[(colon + 1)..];
        var dash = tag.IndexOf('-');
        var size = dash < 0 ? tag : tag[..dash];
        return size.EndsWith('b') ? model[..(colon + 1)] + size : null;
    }

    private static double ParameterSize(string model)
    {
        var family = FamilyAndSize(model);
        if (family is null) return 0;
        var colon = family.LastIndexOf(':');
        return double.TryParse(family[(colon + 1)..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : 0;
    }
}
