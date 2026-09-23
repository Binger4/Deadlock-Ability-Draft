using abilitydraft.Services;
using AbilityDraft.Runtime;

static class PresetChecks
{
    public static void Run()
    {
        var clock = new Clock();
        var service = new PresetTransferService(clock);
        var json = CustomDraftPresetService.Export(new() { DraftMode = abilitydraft.Models.DraftMode.Custom });
        var export = service.Create(json);
        Check(service.IsDownload(export) && service.Download(export) == json, "Preset download preserves the original export JSON");
        var upload = service.Create();
        Check(!service.IsDownload(upload) && service.TakeUpload(upload) is null, "Preset import waits without consuming its browser link");
        service.Upload(upload, json);
        Expect(() => service.Upload(upload, "{}"), "A browser cannot overwrite an uploaded preset");
        Check(service.TakeUpload(upload) == json, "Uploaded preset returns to its waiting game form");
        Expect(() => service.TakeUpload(upload), "Preset uploads are consumed once");
        Expect(() => service.Upload(export, "{}"), "Download links cannot upload settings");
        Expect(() => service.Download("bad"), "Invalid preset tokens cannot read settings");
        Expect(() => service.Upload(service.Create(), new string('x', PresetTransferService.MaxBytes + 1)), "Preset transfer enforces the file size limit");
        clock.Now = clock.Now.AddMinutes(11);
        Expect(() => service.Download(export), "Preset links expire after ten minutes");
        var cancelled = service.Create(); service.Remove(cancelled);
        Expect(() => service.Upload(cancelled, "{}"), "Closing the game form invalidates its upload link");
        var origin = WebsiteNavigation.Origin("http://localhost:5050/");
        Check(WebsiteNavigation.External(origin, "presets/" + export) == "http://localhost:5050/presets/" + export,
            "Native handoff resolves preset links only on the configured website");
        Expect(() => WebsiteNavigation.External(origin, "https://other.example/presets/" + export), "Preset handoff rejects other websites");
        Expect(() => WebsiteNavigation.External(origin, "presets/../../admin"), "Preset handoff rejects unrelated local pages");
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    private static void Expect(Action action, string message) { try { action(); } catch (InvalidOperationException) { Check(true, message); return; } throw new Exception("FAIL: " + message); }
}
