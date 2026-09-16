param(
    [string]$GameDir = 'C:\Steam Games\steamapps\common\Sailwind',
    [Parameter(Mandatory=$true)][string]$LoaderPath
)
$ErrorActionPreference = 'Stop'
# Read-only audit of native default/hardcoded key constants. Custom user bindings are
# outside this claim. No native source or DLL is copied into the project/output.
Add-Type -Path (Join-Path $LoaderPath 'Mono.Cecil.dll')
$managed = Join-Path $GameDir 'Sailwind_Data\Managed'
$core = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'UnityEngine.CoreModule.dll'))
try {
    $keyType = $core.MainModule.Types | Where-Object FullName -EQ 'UnityEngine.KeyCode'
    $homeCode = [int](($keyType.Fields | Where-Object Name -EQ 'Home').Constant)
    if ($homeCode -ne 278) { throw 'Installed Home key enum changed. Reinspect audit.' }
} finally { $core.Dispose() }
function Get-AllTypes($types) {
    foreach ($type in $types) {
        $type
        Get-AllTypes $type.NestedTypes
    }
}
$reports = @()
$hits = @()
$inputCalls = 0
foreach ($name in @('Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','Oculus.VR.dll')) {
    $path = Join-Path $managed $name
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
    try {
        foreach ($type in (Get-AllTypes $assembly.MainModule.Types)) {
            foreach ($method in $type.Methods) {
                if (-not $method.HasBody) { continue }
                foreach ($instruction in $method.Body.Instructions) {
                    $opcode = $instruction.OpCode.Code.ToString()
                    if (($opcode -eq 'Ldc_I4' -and [int]$instruction.Operand -eq $homeCode) -or
                        ($opcode -eq 'Ldstr' -and [string]$instruction.Operand -ieq 'home')) {
                        $hits += "$name $($method.FullName) IL_$($instruction.Offset.ToString('X4'))"
                    }
                    if ($instruction.Operand -is [Mono.Cecil.MethodReference] -and
                        $instruction.Operand.DeclaringType.FullName -eq 'UnityEngine.Input' -and
                        $instruction.Operand.Name -match '^Get(Key|Button|Axis)') { $inputCalls++ }
                }
            }
        }
    } finally { $assembly.Dispose() }
    $reports += @{ assembly=$name; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[pscustomobject]@{ HomeKeyCode=$homeCode; NativeInputCalls=$inputCalls; HomeConstantHits=$hits; Assemblies=$reports } | ConvertTo-Json -Depth 5
if ($hits.Count -gt 0) { throw 'Home is referenced by installed native game code. Reevaluate default.' }
