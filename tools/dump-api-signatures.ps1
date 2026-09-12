$ErrorActionPreference='Stop'
$asm=[System.Reflection.Assembly]::LoadFrom('C:\Users\jack\swagent\refs\SolidWorks.Interop.sldworks.dll')
$types=$asm.GetTypes()
function Dump($tn,$pattern){
  $ty=$types|?{$_.Name -eq $tn}|select -First 1
  if(-not $ty){Write-Output "TYPE NOT FOUND: $tn";return}
  $ms=$ty.GetMethods()|?{$_.Name -match $pattern}
  if(-not $ms){Write-Output "NO MATCH $tn / $pattern";return}
  foreach($m in $ms){
    $ps=($m.GetParameters()|%{ $n=$_.ParameterType.Name; if($_.ParameterType.IsByRef){$n="ref "+$n.TrimEnd('&')}; "$n $($_.Name)" }) -join ', '
    Write-Output "$tn.$($m.Name)($ps) -> $($m.ReturnType.Name)"
  }
}
Write-Output "## Chamfer"
Dump 'IFeatureManager' '^InsertFeatureChamfer|^FeatureChamfer'
Write-Output "`n## Hole wizard"
Dump 'IFeatureManager' '^HoleWizard'
Write-Output "`n## Mirror"
Dump 'IFeatureManager' '^InsertMirrorFeature'
Write-Output "`n## Sketch entities"
Dump 'ISketchManager' '^CreateCornerRectangle$|^CreateCircleByRadius$|^CreateLine$|^CreateCenterLine$|^CreateArc$|^CreateSketchSlot$'
Write-Output "`n## Drawing views"
Dump 'IDrawingDoc' '^Create3rdAngleViews2|^Create1stAngleViews2|^CreateDrawViewFromModelView3|^InsertModelAnnotations3|^CreateSectionViewAt5|^CreateDetailViewAt4'
Write-Output "`n## Export / SaveAs"
Dump 'IModelDocExtension' '^SaveAs'
Write-Output "`n## Mass properties"
Dump 'IModelDocExtension' '^CreateMassProperty'
Dump 'IMassProperty' '^GetMassProperties'
Write-Output "`n## Custom properties"
Dump 'ICustomPropertyManager' '^Get6$|^Get5$|^Add3$|^Set2$'
