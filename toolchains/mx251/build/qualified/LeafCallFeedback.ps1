# 两遍构建仅信任当前源码快照产生的导出叶函数；旧文件不作为反馈输入。
function Get-Mx251LeafBodies([string]$Root,$Config) {
 $symbols=@{}
 foreach($unit in $Config.units) {
  if([IO.Path]::GetExtension($unit.file) -cne '.c' -or $unit.backend -ceq 'legacy'){continue}
  $name=if([IO.Path]::GetExtension($unit.file) -eq '.c'){$unit.name+'.asm'}else{$unit.file}
  $text=[IO.File]::ReadAllText((Join-Path $Root $name))
  foreach($marker in [regex]::Matches($text,'(?m)^; mx251-effects .*$')) {
   $f=ConvertFrom-Mx251EffectsLine $marker.Value 'function';$symbol=$f.function
   if(-not $symbols.ContainsKey($symbol)){$symbols[$symbol]=@()}
   $labels=[regex]::Matches($text,'(?m)^'+[regex]::Escape($symbol)+':\r?$')
   $exported=[regex]::IsMatch($text,'(?m)^\s*\.globl\s+'+[regex]::Escape($symbol)+'\s*$')
   $eligible=$exported -and $f.complete -ceq '1' -and $f.calls -ceq '0' -and (([int]$f.memory -band 2) -eq 0) -and $labels.Count -eq 1 -and $labels[0].Index -lt $marker.Index
   $body=if($eligible){$text.Substring($labels[0].Index,$marker.Index-$labels[0].Index)}else{''}
   $symbols[$symbol]+=@{symbol=$symbol;unit=$unit.name;eligible=$eligible;abi=$f.abi;writes=$f.writes;body=$body;assembly=$name}
  }
 }
 return $symbols
}
function New-Mx251LeafFeedback([string]$Root,$Config,$Inputs,[string]$CompilerSha,[string]$ProfileSha) {
 $pass=Join-Path $Root 'feedback-pass1';[IO.Directory]::CreateDirectory($pass)|Out-Null
 $artifacts=@()
 foreach($unit in $Config.units) {
  foreach($ext in @('.asm','.rel','.lst','.stdout.log','.stderr.log')) {
   $name=$unit.name+$ext;$src=Join-Path $Root $name
   if(Test-Path -LiteralPath $src -PathType Leaf){[IO.File]::Copy($src,(Join-Path $pass $name),$false);$artifacts+=@{path=$name;sha256=(Hash-File $src)}}
  }
 }
 $symbols=Get-Mx251LeafBodies $Root $Config;$leaves=@()
 foreach($key in ($symbols.Keys|Sort-Object -CaseSensitive)) {
  if($symbols[$key].Count -eq 1 -and $symbols[$key][0].eligible){$leaves+=$symbols[$key][0]}
 }
 $path=Join-Path $Root 'leaf-effects.txt'
 $lines=@('MX251-LEAF-EFFECTS-1')+@($leaves|ForEach-Object{$_.symbol+' '+$_.abi+' '+$_.writes})
 [IO.File]::WriteAllText($path,($lines -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
 $record=[ordered]@{schema=1;mode='experimental-leaf-feedback';compilerSha256=$CompilerSha;profileLockSha256=$ProfileSha;inputs=$Inputs;pass1=$artifacts;leaves=$leaves;feedbackSha256=(Hash-File $path);finalValidated=$false}
 Write-Json (Join-Path $Root 'leaf-feedback.json') $record
 return $record
}
function Assert-Mx251LeafFeedback([string]$Root,$Config,$Record,[string]$CompilerSha,[string]$ProfileSha) {
 if($Record.compilerSha256 -cne $CompilerSha -or $Record.profileLockSha256 -cne $ProfileSha -or $Record.feedbackSha256 -cne (Hash-File (Join-Path $Root 'leaf-effects.txt'))){throw 'Leaf feedback identity changed during build'}
 foreach($entry in $Record.inputs.GetEnumerator()) {
  if((Hash-File (Join-Path $Root $entry.Key)) -cne $entry.Value.sha256){throw 'Leaf feedback source snapshot changed during build'}
 }
 foreach($entry in $Record.pass1){if((Hash-File (Join-Path $Root ('feedback-pass1/'+$entry.path))) -cne $entry.sha256){throw 'Leaf feedback first-pass evidence changed'}}
 $final=Get-Mx251LeafBodies $Root $Config
 foreach($leaf in $Record.leaves) {
  if(-not $final.ContainsKey($leaf.symbol) -or $final[$leaf.symbol].Count -ne 1){throw 'Leaf feedback final definition missing or ambiguous'}
  $f=$final[$leaf.symbol][0]
  if(-not $f.eligible -or $f.unit -cne $leaf.unit -or $f.abi -cne $leaf.abi -or $f.writes -cne $leaf.writes -or $f.body -cne $leaf.body){throw "Leaf feedback final definition changed: $($leaf.symbol)"}
 }
}
