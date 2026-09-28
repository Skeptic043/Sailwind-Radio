param(
    [string]$GameAssembly = 'C:\Steam Games\steamapps\common\Sailwind\Sailwind_Data\Managed\Assembly-CSharp.dll',
    [string]$HooksHangMore = '',
    [string]$RadioAssembly = "$PSScriptRoot\..\..\bin\Release\netstandard2.0\SailwindRadio.dll",
    [string]$CecilPath = "$PSScriptRoot\..\..\.local\references\Mono.Cecil.dll",
    [switch]$ShowInstructions
)
$ErrorActionPreference = 'Stop'
Add-Type -Path $CecilPath

function Find-HookMethod($Assembly, [string]$TypeName, [string]$MethodName) {
    $type = $Assembly.MainModule.GetType($TypeName)
    if (-not $type) { throw "Missing type: $TypeName" }
    $methods = @($type.Methods | Where-Object Name -eq $MethodName)
    if ($methods.Count -ne 1) { throw "Expected one method: $TypeName.$MethodName" }
    return $methods[0]
}
function Assert-HookReference($Method, [string]$Reference) {
    if (-not @($Method.Body.Instructions | Where-Object { $_.Operand -and $_.Operand.ToString().Contains($Reference) }).Count) {
        throw "Missing contract reference $Reference in $($Method.FullName)"
    }
}
function Show-HookMethod($Method) {
    Write-Output $Method.FullName
    foreach ($instruction in $Method.Body.Instructions) {
        if ($ShowInstructions -or $instruction.Operand -is [Mono.Cecil.MemberReference]) {
            Write-Output $instruction.ToString()
        }
    }
}

$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($GameAssembly)
$radio = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($RadioAssembly)
try {
    Get-FileHash -LiteralPath $GameAssembly, $RadioAssembly -Algorithm SHA256 | Format-List Path, Hash
    $allow = Find-HookMethod $game 'ShipItem' 'AllowOnItemClick'
    Assert-HookReference $allow 'PickupableItem::big'
    Assert-HookReference $allow 'GoPointerButton::allowPlacingItems'
    Show-HookMethod $allow
    $hangableAllow = Find-HookMethod $game 'ShipItemHangable' 'AllowOnItemClick'
    Assert-HookReference $hangableAllow 'GetComponent<ShipItemLampHook>'
    Show-HookMethod $hangableAllow
    $click = Find-HookMethod $game 'ShipItemLampHook' 'OnItemClick'
    Assert-HookReference $click 'ShipItemLampHook::occupied'
    Assert-HookReference $click 'GetComponent<HangableItem>'
    Assert-HookReference $click 'HangableItem::ConnectJoint'
    Show-HookMethod $click

    $installer = Find-HookMethod $radio 'SailwindRadio.Physical.RadioWorldService' 'TryPatchNativeHooks'
    foreach ($name in @('AllowOnItemClick', 'HookTargetPostfix')) {
        if (-not @($installer.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq $name }).Count) {
            throw "Hook patch installer is missing $name"
        }
    }
    Assert-HookReference $installer 'HarmonyLib.Harmony::Patch'
    $postfix = Find-HookMethod $radio 'SailwindRadio.Physical.RadioWorldService' 'HookTargetPostfix'
    if (($postfix.Parameters.Name -join ',') -ne '__instance,lookedAtButton,__result' -or
        $postfix.Parameters[2].ParameterType.FullName -ne 'System.Boolean&') { throw 'Hook postfix Harmony parameter contract changed' }
    foreach ($reference in @('::active', '::disposed', '::get_HookCompatible', 'ShipItem::sold', 'RadioNativeHooks::AllowsHookTarget')) {
        Assert-HookReference $postfix $reference
    }
    Show-HookMethod $postfix

    if ($HooksHangMore) {
        $hhm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($HooksHangMore)
        try {
            Get-FileHash -LiteralPath $HooksHangMore -Algorithm SHA256 | Format-List Path, Hash
            $hhmAllow = Find-HookMethod $hhm 'HooksHangMore.AttachablePatches/ShipItemPatches' 'AllowOnItemClick'
            Assert-HookReference $hhmAllow 'GetComponent<HooksHangMore.AttachableItem>'
            Assert-HookReference $hhmAllow 'HooksHangMore.AttachableItemHolder::get_IsOccupied'
            Show-HookMethod $hhmAllow
            $hhmClick = Find-HookMethod $hhm 'HooksHangMore.AttachablePatches/ShipItemLampHookPatches' 'OnItemClick'
            Assert-HookReference $hhmClick 'HooksHangMore.AttachableItemHolder::get_IsOccupied'
            Show-HookMethod $hhmClick
            $offsets = Find-HookMethod $hhm 'HooksHangMore.Offsets' '.cctor'
            $names = @($offsets.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' } | ForEach-Object Operand)
            Write-Output ('HHM built-in offset keys matching donor 138 or Radio: ' + @($names | Where-Object { $_ -match '138|radio' }).Count)
        }
        finally { $hhm.Dispose() }
    }
    Write-Output 'Inspected contract references passed. This is static evidence and does not run Unity, Harmony patches or gameplay.'
}
finally { $game.Dispose(); $radio.Dispose() }
