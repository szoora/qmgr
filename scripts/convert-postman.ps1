param(
    [string]$SpecPath,
    [string]$OutPath
)

# Converts a live OpenAPI 3.0 spec (e.g. from GET /swagger/v1/swagger.json) into a Postman
# Collection v2.1, resolving every request body's $ref chain into a real example JSON body rather
# than leaving Postman to show an unresolved schema stub. Usage:
#   curl -k https://localhost:5001/swagger/v1/swagger.json -o swagger.json
#   ./scripts/convert-postman.ps1 -SpecPath swagger.json -OutPath postman/Q-Mgr-API.postman_collection.json
# See docs/API_INTEGRATION_GUIDE.md for how this fits into the integration story.

$spec = Get-Content $SpecPath -Raw | ConvertFrom-Json -Depth 100

function Resolve-Schema($schema, [int]$depth = 0) {
    if ($null -eq $schema) { return $null }
    if ($depth -gt 6) { return $null }

    if ($schema.'$ref') {
        $refName = ($schema.'$ref' -split '/')[-1]
        $target = $spec.components.schemas.$refName
        if ($null -eq $target) { return $null }
        return Resolve-Schema $target ($depth + 1)
    }

    if ($schema.allOf) {
        $merged = [ordered]@{}
        foreach ($sub in $schema.allOf) {
            $r = Resolve-Schema $sub ($depth + 1)
            if ($r -is [System.Collections.IDictionary]) {
                foreach ($k in $r.Keys) { $merged[$k] = $r[$k] }
            }
        }
        return $merged
    }

    if ($schema.enum) {
        return $schema.enum[0]
    }

    switch ($schema.type) {
        'object' {
            $obj = [ordered]@{}
            if ($schema.properties) {
                foreach ($propName in $schema.properties.PSObject.Properties.Name) {
                    $obj[$propName] = Resolve-Schema $schema.properties.$propName ($depth + 1)
                }
            }
            return $obj
        }
        'array' {
            $item = Resolve-Schema $schema.items ($depth + 1)
            return @($item)
        }
        'string' {
            if ($schema.format -eq 'uuid') { return '00000000-0000-0000-0000-000000000000' }
            if ($schema.format -eq 'date-time') { return '2026-08-25T12:00:00Z' }
            if ($schema.format -eq 'date') { return '2026-08-25' }
            return 'string'
        }
        'integer' { return 0 }
        'number' { return 0 }
        'boolean' { return $false }
        default {
            if ($schema.properties) {
                $obj = [ordered]@{}
                foreach ($propName in $schema.properties.PSObject.Properties.Name) {
                    $obj[$propName] = Resolve-Schema $schema.properties.$propName ($depth + 1)
                }
                return $obj
            }
            return $null
        }
    }
}

function ConvertTo-PostmanBody($requestBody) {
    if ($null -eq $requestBody) { return $null }
    $jsonContent = $requestBody.content.'application/json'
    if ($null -eq $jsonContent) { return $null }
    $example = Resolve-Schema $jsonContent.schema
    if ($null -eq $example) { return $null }
    return ($example | ConvertTo-Json -Depth 20)
}

$foldersByTag = [ordered]@{}

foreach ($pathKey in $spec.paths.PSObject.Properties.Name) {
    $pathItem = $spec.paths.$pathKey
    foreach ($method in @('get','post','put','patch','delete')) {
        $op = $pathItem.$method
        if ($null -eq $op) { continue }

        $tag = if ($op.tags -and $op.tags.Count -gt 0) { $op.tags[0] } else { 'Other' }
        if (-not $foldersByTag.Contains($tag)) {
            $foldersByTag[$tag] = [System.Collections.Generic.List[object]]::new()
        }

        # Build path with :variable style for Postman, and a variable list.
        $urlPath = $pathKey
        $pathVariables = [System.Collections.Generic.List[object]]::new()
        if ($op.parameters) {
            foreach ($p in $op.parameters) {
                if ($p.'in' -eq 'path') {
                    $urlPath = $urlPath -replace [regex]::Escape("{$($p.name)}"), ":$($p.name)"
                    $defaultVal = if ($p.schema.format -eq 'uuid') { '00000000-0000-0000-0000-000000000001' } else { '' }
                    $pathVariables.Add([ordered]@{ key = $p.name; value = $defaultVal; description = $p.description })
                }
            }
        }

        $queryParams = [System.Collections.Generic.List[object]]::new()
        if ($op.parameters) {
            foreach ($p in $op.parameters) {
                if ($p.'in' -eq 'query') {
                    $queryParams.Add([ordered]@{ key = $p.name; value = ''; description = $p.description; disabled = -not [bool]$p.required })
                }
            }
        }

        $rawUrl = "{{baseUrl}}$urlPath"
        if ($queryParams.Count -gt 0) {
            $qs = ($queryParams | ForEach-Object { "$($_.key)=" }) -join '&'
            $rawUrl = "$rawUrl?$qs"
        }

        $urlObj = [ordered]@{
            raw = $rawUrl
            host = @('{{baseUrl}}')
            path = @($urlPath.TrimStart('/') -split '/')
        }
        if ($pathVariables.Count -gt 0) { $urlObj['variable'] = $pathVariables }
        if ($queryParams.Count -gt 0) { $urlObj['query'] = $queryParams }

        $headers = [System.Collections.Generic.List[object]]::new()
        $bodyJson = ConvertTo-PostmanBody $op.requestBody
        if ($bodyJson) {
            $headers.Add([ordered]@{ key = 'Content-Type'; value = 'application/json' })
        }

        $item = [ordered]@{
            name = if ($op.summary) { $op.summary } else { "$method $pathKey" }
            request = [ordered]@{
                method = $method.ToUpper()
                header = $headers
                url = $urlObj
            }
        }
        if ($op.description) { $item.request['description'] = $op.description }
        if ($bodyJson) {
            $item.request['body'] = [ordered]@{ mode = 'raw'; raw = $bodyJson; options = [ordered]@{ raw = [ordered]@{ language = 'json' } } }
        }

        $foldersByTag[$tag].Add($item)
    }
}

$collectionItems = [System.Collections.Generic.List[object]]::new()
foreach ($tag in $foldersByTag.Keys) {
    $collectionItems.Add([ordered]@{
        name = $tag
        item = $foldersByTag[$tag]
    })
}

$collection = [ordered]@{
    info = [ordered]@{
        name = 'Q-Mgr API'
        description = 'Q-Mgr queue-management platform API. Generated from the live OpenAPI spec. Set the collection variables baseUrl and accessToken (or apiKey for external-integration endpoints) before running requests. See docs/API_INTEGRATION_GUIDE.md for authentication details.'
        schema = 'https://schema.getpostman.com/json/collection/v2.1.0/collection.json'
    }
    auth = [ordered]@{
        type = 'bearer'
        bearer = @(
            [ordered]@{ key = 'token'; value = '{{accessToken}}'; type = 'string' }
        )
    }
    variable = @(
        [ordered]@{ key = 'baseUrl'; value = 'https://localhost:5001'; type = 'string' }
        [ordered]@{ key = 'accessToken'; value = ''; type = 'string' }
        [ordered]@{ key = 'apiKey'; value = ''; type = 'string' }
    )
    item = $collectionItems
}

$collection | ConvertTo-Json -Depth 40 | Set-Content -Path $OutPath -Encoding UTF8
Write-Output "Wrote $OutPath"
Write-Output ("Folders: " + $foldersByTag.Keys.Count)
Write-Output ("Total requests: " + (($foldersByTag.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum))
