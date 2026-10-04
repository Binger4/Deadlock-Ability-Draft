using DeadworksManaged.Api;

namespace AbilityDraft.Deadworks;

internal static class NativePlayerInput
{
    public static bool HasActivity(CCitadelUserCmdPB command, ref ulong previousButtons)
    {
        var commandActivity = command.ExecuteAbilityIndices != 0 || command.ViewDeltaX.Any(v => v != 0) || command.ViewDeltaY.Any(v => v != 0);
        if (command.Base is not { } input) return commandActivity;
        var buttons = input.ButtonsPb?.Buttonstate1 ?? 0;
        const InputButton heldActions = InputButton.Attack | InputButton.Attack2 | InputButton.Forward | InputButton.Back |
            InputButton.MoveLeft | InputButton.MoveRight | InputButton.MoveUp | InputButton.MoveDown | InputButton.AllAbilities | InputButton.AllItems | InputButton.AllInnates;
        var active = (buttons & (ulong)heldActions) != 0 || buttons != previousButtons ||
            input.Forwardmove != 0 || input.Leftmove != 0 || input.Upmove != 0 || input.Mousedx != 0 || input.Mousedy != 0 || input.Impulse != 0 ||
            commandActivity ||
            input.SubtickMoves.Any(m => m.Button != 0 || m.AnalogForwardDelta != 0 || m.AnalogLeftDelta != 0 || m.PitchDelta != 0 || m.YawDelta != 0);
        previousButtons = buttons;
        return active;
    }
}
