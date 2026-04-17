Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

projectDir = fso.GetParentFolderName(WScript.ScriptFullName)
pythonw = projectDir & "\.venv\Scripts\pythonw.exe"
runPy = projectDir & "\run.py"

If fso.FileExists(pythonw) Then
    shell.CurrentDirectory = projectDir
    shell.Run """" & pythonw & """ """ & runPy & """ ui", 0, False
Else
    MsgBox "The local Python environment is missing. Run Start_Tbot.bat once first.", vbExclamation, "Tbot Ultra"
End If

