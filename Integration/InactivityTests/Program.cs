using AbilityDraft.Deadworks;
using DeadworksManaged.Api;

void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
ulong previous = 0;
var command = new CCitadelUserCmdPB { Base = new CBaseUserCmdPB() };
Check(!NativePlayerInput.HasActivity(command,ref previous), "An empty native command is not activity");
command.Base.ClientTick = 800; command.Base.LegacyCommandNumber = 1234; command.Base.RandomSeed = 72;
command.InShop = true; command.UsingFreeCursor = true;
Check(!NativePlayerInput.HasActivity(command,ref previous), "Tick counters and open shop/cursor state cannot defeat AFK detection");
command.Base.ButtonsPb = new CInButtonStatePB { Buttonstate1 = (ulong)InputButton.DuckToggle };
Check(NativePlayerInput.HasActivity(command,ref previous), "A new toggle press counts as interaction");
Check(!NativePlayerInput.HasActivity(command,ref previous), "A latched toggle does not count forever");
command.Base.ButtonsPb.Buttonstate1 = (ulong)InputButton.Attack;
Check(NativePlayerInput.HasActivity(command,ref previous) && NativePlayerInput.HasActivity(command,ref previous), "Holding fire counts as gameplay");
command.Base.ButtonsPb.Buttonstate1 = 0; NativePlayerInput.HasActivity(command,ref previous);
command.Base.Forwardmove = 1;
Check(NativePlayerInput.HasActivity(command,ref previous), "Movement input counts as activity");
command.Base.Forwardmove = 0; command.Base.Mousedx = 2;
Check(NativePlayerInput.HasActivity(command,ref previous), "Mouse aiming counts as activity");
command.Base.Mousedx = 0; command.Base.SubtickMoves.Add(new CSubtickMoveStep { Button = (ulong)InputButton.Jump, Pressed = true });
Check(NativePlayerInput.HasActivity(command,ref previous), "A short subtick key press is not lost");
command.Base.SubtickMoves.Clear(); command.ExecuteAbilityIndices = 4;
Check(NativePlayerInput.HasActivity(command,ref previous), "Ability execution counts as activity");
Check(NativePlayerInput.HasActivity(new CCitadelUserCmdPB { ExecuteAbilityIndices = 4 },ref previous), "Ability-only packets do not need movement fields to count");
Console.WriteLine("Native AFK input checks passed using Deadworks 0.5.3 command messages.");
