$derBytes = [System.IO.File]::ReadAllBytes('C:\Users\a0812\fccmiddleware\devcert.cer')
$base64 = [System.Convert]::ToBase64String($derBytes, [System.Base64FormattingOptions]::InsertLineBreaks)
$pem = "-----BEGIN CERTIFICATE-----`r`n$base64`r`n-----END CERTIFICATE-----"
[System.IO.File]::WriteAllText('C:\Users\a0812\fccmiddleware\devcert.pem', $pem)
Write-Output "PEM certificate exported"
