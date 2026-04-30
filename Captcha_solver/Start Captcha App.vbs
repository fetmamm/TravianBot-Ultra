Set shell = CreateObject("WScript.Shell")
basePath = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
command = """" & basePath & "\math_ai\.venv\Scripts\pythonw.exe"" """ & basePath & "\math_ai\app.py"""
shell.Run command, 0, False
