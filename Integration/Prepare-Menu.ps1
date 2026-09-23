param(
    [Parameter(Mandatory)][string]$ContentDirectory,
    [Parameter(Mandatory)][string]$GameRoot,
    [string]$DraftServer = '127.0.0.1:27067',
    [string]$PublicQueueServer = '127.0.0.1:27068'
)
$ErrorActionPreference = 'Stop'
if ($DraftServer -notmatch '^[a-zA-Z0-9.-]+:[0-9]{1,5}$' -or [int]($DraftServer.Split(':')[-1]) -notin 1..65535) { throw 'Invalid drafting server address.' }
if ($PublicQueueServer -notmatch '^[a-zA-Z0-9.-]+:[0-9]{1,5}$' -or [int]($PublicQueueServer.Split(':')[-1]) -notin 1..65535 -or $PublicQueueServer -eq $DraftServer) { throw 'Public queue needs a separate valid server address.' }
$cli = Join-Path $PSScriptRoot '../Tools/Source2ViewerCLI/Source2Viewer-CLI.exe'
if (!(Test-Path -LiteralPath $cli)) { throw 'Source2Viewer CLI 20.0 is required to derive the menu from the installed game.' }
$dump = Join-Path $PSScriptRoot 'local/menu-template.xml'
& $cli -i (Join-Path $GameRoot 'game/citadel/pak01_dir.vpk') -o $dump -d -f panorama/layout/citadel_db_page_play.vxml_c | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not decompile the installed play page.' }
[xml]$layout = Get-Content -LiteralPath $dump -Raw
$target = $layout.SelectNodes("//Panel[@id='PlayOptions']")
if ($target.Count -ne 1) { throw 'PlayOptions structure changed; review the new game layout before patching.' }
$scripts = $layout.CreateElement('scripts')
$configInclude = $layout.CreateElement('include'); $configInclude.SetAttribute('src','file://{resources}/scripts/ability_draft_entry_config.js')
$scripts.AppendChild($configInclude) | Out-Null
$include = $layout.CreateElement('include'); $include.SetAttribute('src','file://{resources}/scripts/ability_draft_entry.js')
$scripts.AppendChild($include) | Out-Null
$layout.DocumentElement.PrependChild($scripts) | Out-Null
$styleInclude = $layout.CreateElement('include')
$styleInclude.SetAttribute('src','file://{resources}/styles/ability_draft_entry.css')
$layout.SelectSingleNode('/root/styles').AppendChild($styleInclude) | Out-Null
$fragment = $layout.CreateDocumentFragment()
$fragment.InnerXml = @'
<Panel id="AbilityDraftEntries">
  <Button id="AbilityDraftPublic" class="playoption ADModeCard dim_in_queue" hittest="true" onmouseover="CitadelMusicQueueArpeggiatorNote()">
    <Panel id="UnavailableOverlay"><Label class="unavailable_queue" text="#menu_play_unavailable_in_queue" /></Panel>
    <Panel class="ADCardBacker" />
    <Panel class="ADCardCopy"><Label class="ADModeTitle" text="ABILITY DRAFT" /><Label class="ADModeAction" text="PUBLIC QUEUE" /><Label class="ADModeDescription" text="Find players · Draft your abilities" /></Panel>
  </Button>
  <Button id="AbilityDraftCustom" class="playoption ADModeCard dim_in_queue" hittest="true" onmouseover="CitadelMusicQueueArpeggiatorNote()">
    <Panel id="UnavailableOverlay"><Label class="unavailable_queue" text="#menu_play_unavailable_in_queue" /></Panel>
    <Panel class="ADCardBacker" />
    <Panel class="ADCardCopy"><Label class="ADModeTitle" text="ABILITY DRAFT" /><Label class="ADModeAction" text="CUSTOM LOBBY" /><Label class="ADModeDescription" text="Create a room · Invite by code" /></Panel>
  </Button>
</Panel>
'@
$target[0].AppendChild($fragment) | Out-Null
[IO.File]::WriteAllText((Join-Path $ContentDirectory 'panorama/scripts/ability_draft_entry_config.js'), "var AbilityDraftServer = '$DraftServer'; var AbilityDraftPublicServer = '$PublicQueueServer';", [Text.UTF8Encoding]::new($false))
$layout.Save((Join-Path $ContentDirectory 'panorama/layout/citadel_db_page_play.xml'))
Write-Output 'Added two Ability Draft entries to the installed game play page. Native mode actions are preserved.'
# The engine's connection status can live in a separate Panorama window, outside the
# Deadworks panel tree. Patch its verified layout rather than relying only on traversal.
# This optional mod profile suppresses the native connection notification wherever shown;
# the real 30-second PreGameWait countdown is a different component and remains intact.
$waitingDump = Join-Path $PSScriptRoot 'local/waiting-template.xml'
& $cli -i (Join-Path $GameRoot 'game/citadel/pak01_dir.vpk') -o $waitingDump -d -f panorama/layout/citadel_waiting_for_players_status.vxml_c | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not decompile the installed connecting status.' }
[xml]$waiting = Get-Content -LiteralPath $waitingDump -Raw
$waitingRoot = $waiting.SelectNodes('/root/CitadelWaitingForPlayersStatus')
if ($waitingRoot.Count -ne 1) { throw 'Connecting status layout changed; review before patching.' }
$waitingRoot[0].SetAttribute('style', 'visibility: collapse; opacity: 0; height: 0px;')
$waiting.Save((Join-Path $ContentDirectory 'panorama/layout/citadel_waiting_for_players_status.xml'))
Write-Output 'Suppressed the native connecting notification; normal match preparation countdown is preserved.'
