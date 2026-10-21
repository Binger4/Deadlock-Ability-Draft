namespace abilitydraft.Services;

public static class GameCatalogValidation
{
    // A stale full-catalogue override can remove a new hero's abilities or
    // instantiate an obsolete native class. Check the compiler's actual output.
    public static void VerifyAbilities(Kv3Document source, Kv3Document compiled)
    {
        foreach (var (key, value) in source.Root.Pairs)
        {
            if (value is not Kv3Object expected) continue;
            if (!compiled.Root.TryGetValue(key, out var found) || found is not Kv3Object actual)
                throw new InvalidOperationException($"Compiled ability catalogue is missing '{key}'. Rebuild it from the current game data.");
            VerifyNativeClasses(expected, actual, key);
        }
    }

    private static void VerifyNativeClasses(Kv3Value expected, Kv3Value? actual, string path)
    {
        if (expected is Kv3TypedValue typed) expected = typed.InnerValue;
        if (actual is Kv3TypedValue compiledTyped) actual = compiledTyped.InnerValue;
        if (expected is Kv3Object obj)
        {
            var nativeClass = DeadlockFileParser.GetString(obj, "_class");
            var compiled = actual as Kv3Object;
            if (nativeClass.Length > 0 && (compiled is null || nativeClass != DeadlockFileParser.GetString(compiled, "_class")))
                throw new InvalidOperationException($"Compiled ability '{path}' has the wrong native class. Update the compiler and rebuild the catalogue.");
            foreach (var (key, child) in obj.Pairs)
                VerifyNativeClasses(child, compiled is not null && compiled.TryGetValue(key, out var found) ? found : null, path + "." + key);
        }
        else if (expected is Kv3Array array)
            for (var i = 0; i < array.Items.Count; i++)
                VerifyNativeClasses(array.Items[i], actual is Kv3Array result && i < result.Items.Count ? result.Items[i] : null, path + $"[{i}]");
    }
}
