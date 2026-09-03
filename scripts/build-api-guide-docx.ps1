param(
    [string]$OutPath
)

# ---------- helpers ----------
function Esc([string]$s) {
    if ($null -eq $s) { return '' }
    return $s.Replace('&','&amp;').Replace('<','&lt;').Replace('>','&gt;').Replace('"','&quot;')
}

# Splits "text with **bold** spans" into runs; returns raw <w:r> XML.
function Runs([string]$text, [string]$extraRpr = '') {
    $sb = New-Object System.Text.StringBuilder
    $parts = [regex]::Split($text, '(\*\*[^*]+\*\*)')
    foreach ($p in $parts) {
        if ($p -eq '') { continue }
        if ($p -match '^\*\*(.+)\*\*$') {
            $t = Esc($Matches[1])
            [void]$sb.Append("<w:r><w:rPr><w:b/>$extraRpr</w:rPr><w:t xml:space=`"preserve`">$t</w:t></w:r>")
        } else {
            $t = Esc($p)
            [void]$sb.Append("<w:r><w:rPr>$extraRpr</w:rPr><w:t xml:space=`"preserve`">$t</w:t></w:r>")
        }
    }
    return $sb.ToString()
}

function P([string]$style, [string]$text, [string]$extraRpr = '') {
    $runsXml = Runs $text $extraRpr
    return "<w:p><w:pPr><w:pStyle w:val=`"$style`"/></w:pPr>$runsXml</w:p>"
}

function PlainP([string]$text, [string]$jc = '') {
    $jcXml = if ($jc) { "<w:jc w:val=`"$jc`"/>" } else { '' }
    $runsXml = Runs $text
    return "<w:p><w:pPr>$jcXml</w:pPr>$runsXml</w:p>"
}

function BulletP([string]$text, [int]$numId = 1) {
    $runsXml = Runs $text
    return "<w:p><w:pPr><w:pStyle w:val=`"ListParagraph`"/><w:numPr><w:ilvl w:val=`"0`"/><w:numId w:val=`"$numId`"/></w:numPr></w:pPr>$runsXml</w:p>"
}

function CodeBlock([string]$text) {
    $lines = $text -split "`n"
    $sb = New-Object System.Text.StringBuilder
    foreach ($line in $lines) {
        $t = Esc($line)
        if ($t -eq '') { $t = ' ' }
        [void]$sb.Append("<w:p><w:pPr><w:pStyle w:val=`"CodeBlock`"/></w:pPr><w:r><w:t xml:space=`"preserve`">$t</w:t></w:r></w:p>")
    }
    return $sb.ToString()
}

function Table([string[]]$headers, [array]$rows) {
    $colCount = $headers.Count
    $colWidth = [math]::Floor(9350 / $colCount)
    $grid = ($headers | ForEach-Object { "<w:gridCol w:w=`"$colWidth`"/>" }) -join ''

    $headerCells = ($headers | ForEach-Object {
        $t = Esc($_)
        "<w:tc><w:tcPr><w:tcW w:w=`"$colWidth`" w:type=`"dxa`"/><w:shd w:val=`"clear`" w:fill=`"1F2A37`"/></w:tcPr><w:p><w:pPr><w:jc w:val=`"left`"/></w:pPr><w:r><w:rPr><w:b/><w:color w:val=`"FFFFFF`"/></w:rPr><w:t xml:space=`"preserve`">$t</w:t></w:r></w:p></w:tc>"
    }) -join ''
    $headerRow = "<w:tr>$headerCells</w:tr>"

    $bodyRows = ($rows | ForEach-Object {
        $row = $_
        $cells = ($row | ForEach-Object {
            $runsXml = Runs([string]$_)
            "<w:tc><w:tcPr><w:tcW w:w=`"$colWidth`" w:type=`"dxa`"/></w:tcPr><w:p>$runsXml</w:p></w:tc>"
        }) -join ''
        "<w:tr>$cells</w:tr>"
    }) -join ''

    return "<w:tbl><w:tblPr><w:tblStyle w:val=`"TableGrid`"/><w:tblW w:w=`"9350`" w:type=`"dxa`"/><w:tblBorders><w:top w:val=`"single`" w:sz=`"4`" w:color=`"D9DCE1`"/><w:left w:val=`"single`" w:sz=`"4`" w:color=`"D9DCE1`"/><w:bottom w:val=`"single`" w:sz=`"4`" w:color=`"D9DCE1`"/><w:right w:val=`"single`" w:sz=`"4`" w:color=`"D9DCE1`"/><w:insideH w:val=`"single`" w:sz=`"4`" w:color=`"D9DCE1`"/><w:insideV w:val=`"single`" w:sz=`"4`" w:color=`"D9DCE1`"/></w:tblBorders></w:tblPr><w:tblGrid>$grid</w:tblGrid>$headerRow$bodyRows</w:tbl><w:p/>"
}

# ---------- content ----------
$body = New-Object System.Text.StringBuilder

function Add($xml) { [void]$body.Append($xml) }

Add (P 'DocTitle' 'SACC Software Limited')
Add (P 'DocSubtitle' 'Q-Mgr API Integration Guide')
Add (P 'DocMeta' 'Version 1.0  |  25 August 2026  |  support@getsacc.com')

Add (P 'H1' 'What this is')
Add (PlainP "This guide is for a developer at a partner organisation — a clinic, bank, pharmacy, or any other business — connecting their own system to Q-Mgr's queue. It covers the one thing almost every integration needs: pushing a customer into the queue and reading the queue back. A companion Postman collection covers every other endpoint in the API.")

Add (P 'H1' 'Getting access')
$arrow = [char]0x2192
$accessText = "Ask your Q-Mgr administrator to create an API key for you, from Integrations $arrow API Clients in the Q-Mgr admin panel. They choose which permissions (called **scopes**) your key gets — see the table below — and hand you two values: a **Client ID** and a **Client Secret**."
Add (PlainP $accessText)
Add (PlainP "Both values are needed on every request. The Client Secret is shown once when the key is created, so store it somewhere safe; your administrator can regenerate it at any time, which immediately retires the old one.")

Add (P 'H1' 'Authenticating a request')
Add (PlainP 'Add these two headers to every request:')
Add (CodeBlock "X-API-Key: your-client-id-here`nX-API-Secret: your-client-secret-here")
Add (PlainP "If your HTTP client only lets you set one custom header, send both values in X-API-Key separated by a dot: your-client-id-here.your-client-secret-here.")
Add (PlainP "That's it — no login step, no token that expires and needs refreshing. Every request is checked against your organisation and your key's scopes, so a key issued to one company can never see or touch another company's data, even by guessing.")
Add (PlainP "If you'd rather work with a short-lived bearer token instead of sending the raw key on every call, exchange your Client ID and Secret once at " )
Add (CodeBlock "POST /api/v1/auth/token")
Add (PlainP "and use the token it returns as a normal Authorization: Bearer header. Both approaches reach the same API — pick whichever fits how your system already talks to other services.")

Add (P 'H1' 'Your first request')
Add (PlainP "Here is the whole flow for putting someone into a queue and checking their position. Replace {branchId} with the branch you were given, and your-client-id-here with your real Client ID.")

Add (P 'H2' '1. Create the ticket')
Add (CodeBlock @"
curl -X POST https://your-qmgr-instance/api/v1/branches/{branchId}/tokens \
  -H "X-API-Key: your-client-id-here" \
  -H "X-API-Secret: your-client-secret-here" \
  -H "Content-Type: application/json" \
  -d '{
    "serviceTypeCode": "GEN",
    "customer": { "name": "Jane Doe", "phone": "+15550001234" },
    "externalReference": "YOUR-OWN-ID-4821",
    "externalSystem": "your_system_name"
  }'
"@)
Add (PlainP "The externalReference field is yours to fill in — put whatever ID this person already has in your own system there, so you can look them up again without ever having to store Q-Mgr's own ID anywhere.")

Add (P 'H2' '2. Read back their position')
Add (CodeBlock @"
curl https://your-qmgr-instance/api/v1/branches/{branchId}/tokens/by-reference?externalSystem=your_system_name&externalReference=YOUR-OWN-ID-4821 \
  -H "X-API-Key: your-client-id-here" \
  -H "X-API-Secret: your-client-secret-here"
"@)

Add (P 'H2' '3. Check how busy the branch is')
Add (CodeBlock @"
curl https://your-qmgr-instance/api/v1/branches/{branchId}/queue/status \
  -H "X-API-Key: your-client-id-here" \
  -H "X-API-Secret: your-client-secret-here"
"@)

Add (P 'H1' 'What your key can do')
Add (PlainP "Ask for only the scopes you actually need — a key that just creates tickets shouldn't also be able to cancel them.")
Add (Table @('Scope','What it lets you do') @(
    @('queue:read', 'See how busy a branch is right now — total waiting, average wait time, per-service breakdown'),
    @('queue:write', 'Call, complete, or transfer someone at a counter'),
    @('token:create', 'Put someone into the queue — the endpoint used above'),
    @('token:manage', 'Cancel a ticket, plus everything queue:write allows'),
    @('counter:read', 'See which counters exist and whether they are open'),
    @('service:read', 'See the list of service types (e.g. General, Pharmacy Pickup)'),
    @('stats:read', 'Pull reports and analytics'),
    @('roster:read', 'Read the student/guardian roster'),
    @('roster:write', 'Sync the student/guardian roster from your student information system'),
    @('visitors:read', 'Read visitor records and check-in history'),
    @('visitors:write', 'Check visitors in and out, manage visitor records'),
    @('welfare:read', 'Read student welfare records'),
    @('welfare:write', 'Create and update student welfare records'),
    @('marketing:read', 'Read marketing contacts and broadcasts'),
    @('marketing:send', 'Send marketing broadcasts'),
    @('content:read', 'Read playlists and media for signage'),
    @('content:write', 'Manage playlists and media for signage'),
    @('settings:write', 'Update the display banner')
))

Add (P 'H1' 'Every other endpoint')
Add (PlainP "The three calls above cover most integrations, but the full API — visitor management, feedback, billing, everything the Q-Mgr admin panel itself uses — is documented in the Postman collection that came with this guide.")
Add (PlainP "Import Q-Mgr-API.postman_collection.json and Q-Mgr-Local.postman_environment.json into Postman, set baseUrl to your Q-Mgr instance and apiKey to your Client ID, and every request is already filled in with a working example.")

Add (P 'H1' 'When something goes wrong')
Add (PlainP 'Every error comes back in the same shape:')
Add (CodeBlock @"
{
  "title": "Branch not found",
  "detail": "Branch with ID '...' was not found in your organization.",
  "status": 404
}
"@)
Add (Table @('Status','Meaning') @(
    @('401', 'Your key or secret was not recognised — check both header values'),
    @('403', 'Your key was recognised but does not have the scope for what you tried to do (or the endpoint is not open to API keys)'),
    @('429', 'You have exceeded your key''s per-minute limit — wait for the number of seconds in the Retry-After header'),
    @('404', 'Either the thing does not exist, or it belongs to a different organisation — deliberately indistinguishable, so a wrong guess cannot be used to confirm another company data exists')
))

Add (P 'H1' 'Worth knowing before you build against this')
Add (BulletP 'Each key has its own requests-per-minute limit, set by your administrator, on top of a general per-IP limit. A 429 response carries a Retry-After header.')
Add (BulletP 'Q-Mgr can call your system back: ask your administrator to set a webhook URL on your API client and you will receive signed POSTs (X-QMgr-Signature) when tickets are created, called, served, completed, cancelled or marked no-show. Your system can also push appointment.created / appointment.cancelled events to the inbound endpoint shown on your API client. Details are in the developer guide.')

Add (P 'H1' 'Questions')
Add (PlainP 'support@getsacc.com')

# ---------- OOXML scaffolding ----------

$contentTypes = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
  <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
  <Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>
  <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
  <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
</Types>
'@

$rootRels = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
  <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
</Relationships>
'@

$docRels = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/>
</Relationships>
'@

$core = @"
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <dc:title>Q-Mgr API Integration Guide</dc:title>
  <dc:creator>SACC Software Limited</dc:creator>
  <dc:subject>API Integration</dc:subject>
  <dcterms:created xsi:type="dcterms:W3CDTF">2026-08-25T00:00:00Z</dcterms:created>
  <dcterms:modified xsi:type="dcterms:W3CDTF">2026-08-25T00:00:00Z</dcterms:modified>
</cp:coreProperties>
"@

$app = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties">
  <Company>SACC Software Limited</Company>
</Properties>
'@

$numbering = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:abstractNum w:abstractNumId="0">
    <w:lvl w:ilvl="0">
      <w:start w:val="1"/>
      <w:numFmt w:val="bullet"/>
      <w:lvlText w:val=""/>
      <w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr>
      <w:rPr><w:rFonts w:ascii="Segoe UI Symbol" w:hAnsi="Segoe UI Symbol" w:hint="default"/></w:rPr>
    </w:lvl>
  </w:abstractNum>
  <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
</w:numbering>
'@

$styles = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:docDefaults>
    <w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:lang w:val="en-US"/></w:rPr></w:rPrDefault>
    <w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="288" w:lineRule="auto"/></w:pPr></w:pPrDefault>
  </w:docDefaults>

  <w:style w:type="paragraph" w:default="1" w:styleId="Normal">
    <w:name w:val="Normal"/>
  </w:style>

  <w:style w:type="paragraph" w:styleId="DocTitle">
    <w:name w:val="Doc Title"/>
    <w:basedOn w:val="Normal"/>
    <w:pPr><w:spacing w:before="0" w:after="60"/></w:pPr>
    <w:rPr><w:rFonts w:ascii="Calibri Light" w:hAnsi="Calibri Light"/><w:b/><w:color w:val="1F2A37"/><w:sz w:val="44"/></w:rPr>
  </w:style>

  <w:style w:type="paragraph" w:styleId="DocSubtitle">
    <w:name w:val="Doc Subtitle"/>
    <w:basedOn w:val="Normal"/>
    <w:pPr><w:spacing w:before="0" w:after="60"/></w:pPr>
    <w:rPr><w:color w:val="6B4A57"/><w:sz w:val="30"/></w:rPr>
  </w:style>

  <w:style w:type="paragraph" w:styleId="DocMeta">
    <w:name w:val="Doc Meta"/>
    <w:basedOn w:val="Normal"/>
    <w:pPr><w:spacing w:before="0" w:after="480"/><w:pBdr><w:bottom w:val="single" w:sz="6" w:space="12" w:color="D9DCE1"/></w:pBdr></w:pPr>
    <w:rPr><w:color w:val="6B7280"/><w:sz w:val="20"/></w:rPr>
  </w:style>

  <w:style w:type="paragraph" w:styleId="H1">
    <w:name w:val="heading 1"/>
    <w:basedOn w:val="Normal"/>
    <w:pPr><w:spacing w:before="360" w:after="160"/><w:keepNext/></w:pPr>
    <w:rPr><w:rFonts w:ascii="Calibri Light" w:hAnsi="Calibri Light"/><w:b/><w:color w:val="7A2847"/><w:sz w:val="30"/></w:rPr>
  </w:style>

  <w:style w:type="paragraph" w:styleId="H2">
    <w:name w:val="heading 2"/>
    <w:basedOn w:val="Normal"/>
    <w:pPr><w:spacing w:before="240" w:after="120"/><w:keepNext/></w:pPr>
    <w:rPr><w:b/><w:color w:val="1F2A37"/><w:sz w:val="24"/></w:rPr>
  </w:style>

  <w:style w:type="paragraph" w:styleId="CodeBlock">
    <w:name w:val="Code Block"/>
    <w:basedOn w:val="Normal"/>
    <w:pPr><w:spacing w:before="0" w:after="0"/><w:shd w:val="clear" w:fill="F5F6F8"/><w:ind w:left="120" w:right="120"/></w:pPr>
    <w:rPr><w:rFonts w:ascii="Consolas" w:hAnsi="Consolas"/><w:color w:val="1F2A37"/><w:sz w:val="19"/></w:rPr>
  </w:style>

  <w:style w:type="paragraph" w:styleId="ListParagraph">
    <w:name w:val="List Paragraph"/>
    <w:basedOn w:val="Normal"/>
    <w:pPr><w:spacing w:after="80"/></w:pPr>
  </w:style>

  <w:style w:type="table" w:styleId="TableGrid">
    <w:name w:val="Table Grid"/>
    <w:tblPr><w:tblCellMar><w:top w:w="80" w:type="dxa"/><w:left w:w="120" w:type="dxa"/><w:bottom w:w="80" w:type="dxa"/><w:right w:w="120" w:type="dxa"/></w:tblCellMar></w:tblPr>
  </w:style>
</w:styles>
'@

$documentXml = @"
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
    $($body.ToString())
    <w:sectPr>
      <w:pgSz w:w="12240" w:h="15840"/>
      <w:pgMar w:top="1000" w:right="1200" w:bottom="1000" w:left="1200" w:header="720" w:footer="720" w:gutter="0"/>
    </w:sectPr>
  </w:body>
</w:document>
"@

# ---------- zip it up ----------
if (Test-Path $OutPath) { Remove-Item $OutPath -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::Open($OutPath, [System.IO.Compression.ZipArchiveMode]::Create)

function AddEntry($zip, $entryName, $content) {
    $entry = $zip.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    $writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false)))
    $writer.Write($content)
    $writer.Flush()
    $writer.Close()
    $stream.Close()
}

AddEntry $zip '[Content_Types].xml' $contentTypes
AddEntry $zip '_rels/.rels' $rootRels
AddEntry $zip 'docProps/core.xml' $core
AddEntry $zip 'docProps/app.xml' $app
AddEntry $zip 'word/document.xml' $documentXml
AddEntry $zip 'word/styles.xml' $styles
AddEntry $zip 'word/numbering.xml' $numbering
AddEntry $zip 'word/_rels/document.xml.rels' $docRels

$zip.Dispose()

Write-Output "Wrote $OutPath"
