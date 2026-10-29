# 器件规则只描述已验证的启动与地址窗口；不能推断整颗芯片的全部容量或外设能力。
function Get-Mx251TargetContract([string]$Target) {
 $registryPath=Join-Path $PSScriptRoot 'TargetContracts.json'
 $registry=Get-Content -LiteralPath $registryPath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
 if($registry.schema -ne 1){throw 'Unsupported target contract registry'}
 Choice $Target @($registry.targets.Keys) 'target'
 $contract=$registry.targets[$Target]
 if($contract.id -cne $Target){throw 'Target contract identity mismatch'}
 return $contract
}

function Assert-Mx251TargetProfile($Lock,$Contract,[string]$ProfileRoot,[string]$Runtime) {
 # schema 2 不再允许 profile 另存一套可漂移的向量/中断/MDU 配置。
 Keys-Only $Lock @('schema','target','mx251Version','toolchain','profile','compilerOverlay','targetContract') 'profile lock'
 if($Lock.schema -ne 2){throw 'Target contract requires profile lock schema 2'}
 if($Lock.target -cne $Contract.id){throw 'Target profile mismatch'}
 Keys-Only $Lock.targetContract @('id','registrySha256') 'target contract reference'
 if($Lock.targetContract.id -cne $Contract.id -or $Lock.targetContract.registrySha256 -cne (Hash-File (Join-Path $PSScriptRoot 'TargetContracts.json'))){throw 'Target contract lock mismatch'}
 if($Runtime -cnotin $Contract.allowedRuntimes){throw "Runtime $Runtime is not qualified for target $($Contract.id)"}
 if(@($Lock.profile | Where-Object { $_.path -ceq 'startup.asm' -and $_.sha256 -ceq $Contract.startup.sha256 }).Count -ne 1 -or (Hash-File (Relative-File $ProfileRoot 'startup.asm')) -cne $Contract.startup.sha256){throw 'Startup does not match target contract'}
 if(-not $Lock.ContainsKey('compilerOverlay')){throw 'Target contract requires a pinned compiler overlay'}
 # 存在库文件不等于已经锁定；按实际链接的库逐个要求唯一清单项。
 $libraries=@()
 if($Runtime -match '^native-math'){$libraries+='runtime/libnativefloat.lib'}
 if($Runtime -in @('native-int','native-math','native-math-wide')){$libraries+='runtime/libnativeint.lib'}
 if($Runtime.EndsWith('-mdu32')){
  if($null -eq $Contract.mdu32 -or $Contract.mdu32.abi -cne 'mcs251-r4' -or $Contract.mdu32.trigger -cne 'mov-direct-immediate'){throw 'This target contract has no qualified MDU32 ABI/trigger'}
  $libraries+='runtime/libai8051umdu32.lib'
 }
 foreach($library in $libraries){if(@($Lock.profile|Where-Object{$_.path -ceq $library}).Count -ne 1){throw "Runtime library must be pinned exactly once: $library"}}
}
