using System.Text.Json;
using System.Text;

namespace HeatTurbo.Services;

public sealed record DriverDeviceInfo(
    string DeviceId,
    string Device,
    string Manufacturer,
    string Provider,
    string Version,
    string Date,
    string Kind,
    string Role,
    bool IsDedicatedGpu,
    bool IsSigned);

public sealed record DriverUpdateInfo(
    string UpdateId,
    int RevisionNumber,
    string Title,
    string DriverClass,
    string Manufacturer,
    string Provider,
    string Model,
    string Version,
    string Date,
    string HardwareId,
    long SizeBytes,
    bool IsDownloaded,
    bool MayRequireRestart);

public sealed record DriverScanResult(
    bool IsSupported,
    bool InventorySucceeded,
    bool UpdateScanSucceeded,
    string Source,
    string Message,
    IReadOnlyList<DriverDeviceInfo> Devices,
    IReadOnlyList<DriverUpdateInfo> AvailableUpdates,
    DateTimeOffset ScannedAt);

public sealed record DriverInstallItemResult(
    string UpdateId,
    string Title,
    bool Success,
    bool Warning,
    string Status,
    string ErrorCode,
    bool RebootRequired);

public sealed record DriverInstallResult(
    bool Success,
    string Message,
    int Found,
    int Downloaded,
    int Installed,
    int Warnings,
    int Failed,
    bool RebootRequired,
    IReadOnlyList<DriverInstallItemResult> Items);

public sealed record DriverUpdateSelection(string UpdateId, int RevisionNumber);
public sealed record DriverInstallRequest(IReadOnlyList<DriverUpdateSelection>? Updates);
public sealed record DriverInstallStartResult(bool Success, string Message, Guid? OperationId);
public sealed record DriverInstallOperation(
    Guid OperationId,
    bool IsRunning,
    string Phase,
    string Message,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    DriverInstallResult? Result);

public sealed class DriverService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly RestorePointService _restorePoints;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _installStateLock = new();
    private DriverScanResult? _cachedScan;
    private DriverInstallOperation? _installOperation;
    private Task? _installTask;

    public DriverService(RestorePointService restorePoints) => _restorePoints = restorePoints;

    /// <summary>
    /// Reads the installed display/chipset devices and asks Windows Update for
    /// driver packages that Windows considers applicable to this exact machine.
    /// Firmware and BIOS packages are deliberately excluded.
    /// </summary>
    public async Task<DriverScanResult> ScanAsync(CancellationToken ct, bool refresh = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(false, false, false, "Windows Update", "A verificação de drivers só está disponível no Windows.",
                [], [], DateTimeOffset.UtcNow);
        }

        if (!refresh && _cachedScan is { } cached && DateTimeOffset.UtcNow - cached.ScannedAt < TimeSpan.FromMinutes(2))
            return cached;

        await _operationGate.WaitAsync(ct);
        try
        {
            if (!refresh && _cachedScan is { } current && DateTimeOffset.UtcNow - current.ScannedAt < TimeSpan.FromMinutes(2))
                return current;

            DriverDeviceInfo[] devices = [];
            DriverUpdateInfo[] updates = [];
            string? inventoryError = null;
            string? updateError = null;

            try
            {
                var json = await SystemInfoService.RunPowerShellAsync(
                    InventoryScript, ct, TimeSpan.FromMinutes(2));
                var wire = JsonSerializer.Deserialize<DriverInventoryWire>(json, JsonOptions)
                    ?? throw new InvalidOperationException("O Windows não retornou o inventário de dispositivos.");
                devices = (wire.Devices ?? [])
                    .Where(x => !string.IsNullOrWhiteSpace(x.Device))
                    .Select(x => new DriverDeviceInfo(
                        Clean(x.DeviceId), Clean(x.Device, "Dispositivo sem nome"), Clean(x.Manufacturer),
                        Clean(x.Provider), Clean(x.Version), Clean(x.Date), Clean(x.Kind), Clean(x.Role),
                        x.IsDedicatedGpu, x.IsSigned))
                    .ToArray();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                inventoryError = "Não foi possível ler o inventário de dispositivos. " + ex.Message.Trim();
            }

            try
            {
                var json = await SystemInfoService.RunPowerShellAsync(
                    UpdateScanScript, ct, TimeSpan.FromMinutes(5));
                var wire = JsonSerializer.Deserialize<DriverUpdatesWire>(json, JsonOptions)
                    ?? throw new InvalidOperationException("O Windows Update retornou uma resposta vazia.");
                updates = (wire.Updates ?? [])
                    .Where(x => !string.IsNullOrWhiteSpace(x.UpdateId) && !string.IsNullOrWhiteSpace(x.Title))
                    .Select(x => new DriverUpdateInfo(
                        Clean(x.UpdateId), x.RevisionNumber, Clean(x.Title), Clean(x.DriverClass),
                        Clean(x.Manufacturer), Clean(x.Provider), Clean(x.Model), Clean(x.Version),
                        Clean(x.Date), Clean(x.HardwareId), Math.Max(0, x.SizeBytes), x.IsDownloaded,
                        x.MayRequireRestart))
                    .ToArray();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                updateError = FriendlyWindowsUpdateError(ex);
            }

            var inventorySucceeded = inventoryError is null;
            var updateScanSucceeded = updateError is null;
            var message = updateError ?? inventoryError ?? (updates.Length == 0
                ? "Os drivers de vídeo e chipset já estão em dia pelo Windows Update."
                : $"{updates.Length} atualização(ões) aplicável(is) encontrada(s).");
            if (updateError is not null && devices.Length > 0)
                message = "Hardware identificado, mas a consulta online falhou. " + updateError;
            else if (inventoryError is not null && updates.Length > 0)
                message = $"{updates.Length} atualização(ões) encontrada(s), mas o inventário local falhou. {inventoryError}";

            var result = new DriverScanResult(true, inventorySucceeded, updateScanSucceeded, "Windows Update",
                message, devices, updates, DateTimeOffset.UtcNow);
            if (inventorySucceeded && updateScanSucceeded) _cachedScan = result;
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Downloads and installs only applicable display/chipset packages returned
    /// by Windows Update. The Windows Update Agent performs hardware-ID matching,
    /// download validation and package installation. It never installs firmware.
    /// </summary>
    public bool IsInstallationRunning
    {
        get
        {
            lock (_installStateLock)
                return _installOperation?.IsRunning == true && _installTask?.IsCompleted != true;
        }
    }

    public DriverInstallOperation? GetInstallOperation()
    {
        lock (_installStateLock) return _installOperation;
    }

    public DriverInstallStartResult StartInstall(DriverInstallRequest? request)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, "A instalação de drivers só está disponível no Windows.", null);

        var requestedUpdates = (request?.Updates ?? [])
            .Where(update => Guid.TryParse(update.UpdateId, out _) && update.RevisionNumber >= 0)
            .DistinctBy(update => $"{update.UpdateId}:{update.RevisionNumber}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestedUpdates.Length == 0)
            return new(false, "Nenhum pacote válido foi selecionado. Verifique os drivers novamente.", null);

        lock (_installStateLock)
        {
            if (_installOperation?.IsRunning == true)
                return new(true, "A instalação de drivers já está em andamento.", _installOperation.OperationId);

            var operationId = Guid.NewGuid();
            _installOperation = new(operationId, true, "backup",
                "Criando e verificando o ponto de restauração...", DateTimeOffset.UtcNow, null, null);
            _installTask = Task.Run(() => RunInstallOperationAsync(operationId, requestedUpdates));
            return new(true, "Instalação iniciada. O HeatTurbo acompanhará o resultado dentro do app.", operationId);
        }
    }

    private async Task RunInstallOperationAsync(Guid operationId, DriverUpdateSelection[] requestedUpdates)
    {
        DriverInstallResult result;
        try
        {
            result = await InstallSelectedUpdatesAsync(operationId, requestedUpdates);
        }
        catch (Exception ex)
        {
            _cachedScan = null;
            result = Failure("A operação de drivers terminou de forma inesperada. " + FriendlyWindowsUpdateError(ex));
        }

        lock (_installStateLock)
        {
            if (_installOperation?.OperationId != operationId) return;
            _installOperation = _installOperation with
            {
                IsRunning = false,
                Phase = result.Success ? "completed" : "failed",
                Message = result.Message,
                CompletedAt = DateTimeOffset.UtcNow,
                Result = result
            };
        }
    }

    private async Task<DriverInstallResult> InstallSelectedUpdatesAsync(
        Guid operationId,
        DriverUpdateSelection[] requestedUpdates)
    {
        UpdateInstallOperation(operationId, "backup", "Criando e verificando o ponto de restauração...");
        var backup = await _restorePoints.EnsureBeforeChangeAsync(CancellationToken.None);
        if (!backup.Success)
            return Failure("A instalação foi cancelada porque o ponto de restauração não ficou pronto. " + backup.Message);

        UpdateInstallOperation(operationId, "installing",
            "O Windows Update está baixando, validando e instalando os pacotes selecionados...");
        await _operationGate.WaitAsync(CancellationToken.None);
        try
        {
            var script = BuildInstallScript(requestedUpdates);
            var json = await SystemInfoService.RunPowerShellAsync(
                script, CancellationToken.None, TimeSpan.FromMinutes(45));
            var wire = JsonSerializer.Deserialize<DriverInstallWire>(json, JsonOptions)
                ?? throw new InvalidOperationException("O Windows Update não informou o resultado da instalação.");

            var items = (wire.Items ?? [])
                .Select(x => new DriverInstallItemResult(
                    Clean(x.UpdateId), Clean(x.Title), x.Success, x.Warning, Clean(x.Status), Clean(x.ErrorCode),
                    x.RebootRequired))
                .ToArray();

            _cachedScan = null;
            var success = wire.Failed == 0;
            string message;
            if (wire.Found == 0)
                message = "Nenhuma atualização aplicável de vídeo ou chipset foi encontrada.";
            else if (success)
                message = $"{wire.Installed} driver(s) instalado(s) pelo Windows Update." +
                          (wire.Warnings > 0 ? $" {wire.Warnings} concluído(s) com avisos." : string.Empty) +
                          (wire.RebootRequired ? " Reinicie o Windows para concluir." : string.Empty);
            else
                message = $"{wire.Installed} driver(s) instalado(s) e {wire.Failed} falharam. Confira os detalhes.";

            return new(success, message, wire.Found, wire.Downloaded, wire.Installed, wire.Warnings, wire.Failed,
                wire.RebootRequired, items);
        }
        catch (Exception ex)
        {
            _cachedScan = null;
            return Failure(FriendlyWindowsUpdateError(ex) +
                " O resultado pode ser parcial; verifique novamente os drivers antes de repetir.");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void UpdateInstallOperation(Guid operationId, string phase, string message)
    {
        lock (_installStateLock)
        {
            if (_installOperation?.OperationId != operationId || !_installOperation.IsRunning) return;
            _installOperation = _installOperation with { Phase = phase, Message = message };
        }
    }

    private static DriverInstallResult Failure(string message) =>
        new(false, message, 0, 0, 0, 0, 0, false, []);

    private static string Clean(string? value, string fallback = "—") =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string FriendlyWindowsUpdateError(Exception ex)
    {
        var details = ex.Message.Trim();
        if (details.Contains("0x80240044", StringComparison.OrdinalIgnoreCase))
            return "O Windows Update recusou a operação. Abra o HeatTurbo como administrador.";
        if (details.Contains("0x8024001E", StringComparison.OrdinalIgnoreCase))
            return "O serviço Windows Update está parado ou encerrando. Inicie-o e tente novamente.";
        if (details.Contains("0x8024402C", StringComparison.OrdinalIgnoreCase) ||
            details.Contains("0x80072EE7", StringComparison.OrdinalIgnoreCase))
            return "O Windows Update não conseguiu acessar a internet. Verifique DNS, proxy e conexão.";
        if (details.Contains("0x8024401C", StringComparison.OrdinalIgnoreCase) ||
            details.Contains("0x80072EE2", StringComparison.OrdinalIgnoreCase))
            return "A consulta ao Windows Update expirou. Tente novamente em alguns minutos.";
        if (details.Contains("0x8024002E", StringComparison.OrdinalIgnoreCase))
            return "O acesso ao Windows Update está desativado por uma política do Windows.";

        return "O Windows Update não conseguiu concluir a operação. " + details;
    }

    private sealed record DriverInventoryWire(DriverDeviceWire[]? Devices);
    private sealed record DriverUpdatesWire(DriverUpdateWire[]? Updates);
    private sealed record DriverDeviceWire(
        string? DeviceId, string? Device, string? Manufacturer, string? Provider, string? Version,
        string? Date, string? Kind, string? Role, bool IsDedicatedGpu, bool IsSigned);
    private sealed record DriverUpdateWire(
        string? UpdateId, int RevisionNumber, string? Title, string? DriverClass, string? Manufacturer,
        string? Provider, string? Model, string? Version, string? Date, string? HardwareId,
        long SizeBytes, bool IsDownloaded, bool MayRequireRestart);
    private sealed record DriverInstallWire(
        int Found, int Downloaded, int Installed, int Warnings, int Failed, bool RebootRequired,
        DriverInstallItemWire[]? Items);
    private sealed record DriverInstallItemWire(
        string? UpdateId, string? Title, bool Success, bool Warning, string? Status, string? ErrorCode,
        bool RebootRequired);

    private const string SharedPowerShellFunctions = """
        function Get-SafeValue([scriptblock]$Expression, $Fallback = '') {
            try {
                $value = & $Expression
                if ($null -eq $value) { return $Fallback }
                return $value
            }
            catch { return $Fallback }
        }

        function Get-CategoryText($Update) {
            $names = @()
            try {
                for ($index = 0; $index -lt $Update.Categories.Count; $index++) {
                    $names += [string]$Update.Categories.Item($index).Name
                }
            }
            catch { }
            return ($names -join ' ')
        }

        function Test-IsFirmware($Update) {
            $driverClass = [string](Get-SafeValue { $Update.DriverClass })
            $title = [string](Get-SafeValue { $Update.Title })
            $model = [string](Get-SafeValue { $Update.DriverModel })
            $categories = Get-CategoryText $Update
            $text = "$driverClass $title $model $categories"
            return $driverClass -match '^(?i:Firmware)$' -or $text -match '(?i)(firmware|\bBIOS\b|\bUEFI\b)'
        }

        function Test-IsSupportedDriver($Update) {
            if (Test-IsFirmware $Update) { return $false }
            $driverClass = [string](Get-SafeValue { $Update.DriverClass })
            $manufacturer = [string](Get-SafeValue { $Update.DriverManufacturer })
            $provider = [string](Get-SafeValue { $Update.DriverProvider })
            $title = [string](Get-SafeValue { $Update.Title })
            if ($driverClass -eq 'Display') { return $true }
            if ($driverClass -notin @('System', 'Extension', 'SoftwareComponent')) { return $false }
            return "$manufacturer $provider $title" -match '(?i)(NVIDIA|Advanced Micro Devices|\bAMD\b|\bIntel\b)' -and
                "$driverClass $title" -match '(?i)(chipset|system|PCI|SMBus|GPIO|I2C|PSP|management engine|software component|extension)'
        }

        function Get-ApplicableDriverUpdates($Session) {
            $searcher = $Session.CreateUpdateSearcher()
            $searchResult = $searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0")
            if ([int]$searchResult.ResultCode -ge 4) {
                throw "A pesquisa do Windows Update falhou (resultado $([int]$searchResult.ResultCode))."
            }
            $items = @()
            for ($index = 0; $index -lt $searchResult.Updates.Count; $index++) {
                $update = $searchResult.Updates.Item($index)
                if (Test-IsSupportedDriver $update) { $items += $update }
            }
            return @($items)
        }
        """;

    private const string InventoryScript = """
        $ErrorActionPreference = 'Stop'
        $signedDrivers = @(Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop | Where-Object {
            $_.DeviceName -and ($_.DeviceClass -eq 'DISPLAY' -or
            ($_.DeviceClass -eq 'SYSTEM' -and
             "$($_.DeviceName) $($_.Manufacturer) $($_.DriverProviderName)" -match '(?i)(NVIDIA|Advanced Micro Devices|\bAMD\b|\bIntel\b|chipset|SMBus|PCI Express|Platform Security|Management Engine|GPIO|I2C|PSP)'))
        })

        $devices = @()
        foreach ($driver in $signedDrivers) {
            $name = [string]$driver.DeviceName
            $isDisplay = [string]$driver.DeviceClass -eq 'DISPLAY'
            $isKnownDedicated = $name -match '(?i)(NVIDIA|GeForce|RTX|Radeon RX|Radeon Pro|Intel.*Arc.*\b[AB]\d{3})'
            $isIntegrated = $name -match '(?i)(Radeon(?:\(TM\))? Graphics|Intel.*(?:UHD|Iris|HD Graphics))' -or
                ($name -match '(?i)Intel.*Arc' -and -not $isKnownDedicated)
            $isDedicated = $isDisplay -and -not $isIntegrated -and $isKnownDedicated
            $role = if ($isDedicated) { 'GPU dedicada' } elseif ($isDisplay) { 'GPU integrada' } else { 'Chipset / sistema' }
            $date = ''
            if ($driver.DriverDate) {
                try { $date = ([datetime]$driver.DriverDate).ToString('yyyy-MM-dd') } catch { $date = [string]$driver.DriverDate }
            }
            $devices += [pscustomobject]@{
                deviceId = [string]$driver.DeviceID
                device = $name
                manufacturer = [string]$driver.Manufacturer
                provider = [string]$driver.DriverProviderName
                version = [string]$driver.DriverVersion
                date = $date
                kind = [string]$driver.DeviceClass
                role = $role
                isDedicatedGpu = [bool]$isDedicated
                isSigned = [bool]$driver.IsSigned
            }
        }
        $devices = @($devices | Sort-Object @{ Expression = { if ($_.isDedicatedGpu) { 0 } elseif ($_.kind -eq 'DISPLAY') { 1 } else { 2 } } }, device -Unique | Select-Object -First 30)
        [pscustomobject]@{ devices = @($devices) } | ConvertTo-Json -Depth 4 -Compress
        """;

    private static readonly string UpdateScanScript = $$"""
        $ErrorActionPreference = 'Stop'
        {{SharedPowerShellFunctions}}

        $session = New-Object -ComObject Microsoft.Update.Session
        $session.ClientApplicationID = 'HeatTurbo'
        $applicable = @(Get-ApplicableDriverUpdates $session)
        $updates = @()
        foreach ($update in $applicable) {
            $rebootBehavior = [int](Get-SafeValue { $update.InstallationBehavior.RebootBehavior } 0)
            $date = Get-SafeValue { ([datetime]$update.DriverVerDate).ToString('yyyy-MM-dd') }
            $title = [string]$update.Title
            $version = [string](Get-SafeValue { $update.DriverVersion })
            if (-not $version -and $title -match '(\d+(?:\.\d+){1,4})\s*$') { $version = $Matches[1] }
            $updates += [pscustomobject]@{
                updateId = [string]$update.Identity.UpdateID
                revisionNumber = [int]$update.Identity.RevisionNumber
                title = $title
                driverClass = [string](Get-SafeValue { $update.DriverClass })
                manufacturer = [string](Get-SafeValue { $update.DriverManufacturer })
                provider = [string](Get-SafeValue { $update.DriverProvider })
                model = [string](Get-SafeValue { $update.DriverModel })
                version = $version
                date = [string]$date
                hardwareId = [string](Get-SafeValue { $update.DriverHardwareID })
                sizeBytes = [int64](Get-SafeValue { $update.MaxDownloadSize } 0)
                isDownloaded = [bool]$update.IsDownloaded
                mayRequireRestart = $rebootBehavior -ne 0
            }
        }

        [pscustomobject]@{ updates = @($updates) } | ConvertTo-Json -Depth 5 -Compress
        """;

    private static string BuildInstallScript(IReadOnlyList<DriverUpdateSelection> requestedUpdates)
    {
        var requestedJson = JsonSerializer.Serialize(requestedUpdates, JsonOptions);
        var encodedRequested = Convert.ToBase64String(Encoding.UTF8.GetBytes(requestedJson));
        return $$"""
        $ErrorActionPreference = 'Stop'
        {{SharedPowerShellFunctions}}

        $requestedJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{encodedRequested}}'))
        $requested = @($requestedJson | ConvertFrom-Json)

        function Test-WasRequested($Update) {
            $updateId = [string]$Update.Identity.UpdateID
            $revision = [int]$Update.Identity.RevisionNumber
            return @($requested | Where-Object {
                [string]$_.UpdateId -eq $updateId -and [int]$_.RevisionNumber -eq $revision
            }).Count -gt 0
        }

        function Convert-ResultCode([int]$Code) {
            switch ($Code) {
                0 { 'Não iniciado' }
                1 { 'Em andamento' }
                2 { 'Instalado' }
                3 { 'Instalado com avisos' }
                4 { 'Falhou' }
                5 { 'Cancelado' }
                default { "Resultado $Code" }
            }
        }

        function Convert-HResult($Value) {
            if ($null -eq $Value -or [int64]$Value -eq 0) { return '' }
            $unsigned = [int64]$Value -band [int64]([uint32]::MaxValue)
            return '0x' + $unsigned.ToString('X8')
        }

        $session = New-Object -ComObject Microsoft.Update.Session
        $session.ClientApplicationID = 'HeatTurbo'
        $applicable = @(Get-ApplicableDriverUpdates $session | Where-Object { Test-WasRequested $_ })
        $selectionFailures = @()
        foreach ($selection in $requested) {
            $stillAvailable = @($applicable | Where-Object {
                [string]$_.Identity.UpdateID -eq [string]$selection.UpdateId -and
                [int]$_.Identity.RevisionNumber -eq [int]$selection.RevisionNumber
            }).Count -gt 0
            if (-not $stillAvailable) {
                $selectionFailures += [pscustomobject]@{
                    updateId = [string]$selection.UpdateId
                    title = 'Pacote selecionado não está mais disponível'
                    success = $false
                    warning = $false
                    status = 'O catálogo mudou; verifique os drivers novamente'
                    errorCode = ''
                    rebootRequired = $false
                }
            }
        }
        if ($applicable.Count -eq 0) {
            [pscustomobject]@{ found = $requested.Count; downloaded = 0; installed = 0; warnings = 0; failed = $selectionFailures.Count; rebootRequired = $false; items = @($selectionFailures) } |
                ConvertTo-Json -Depth 5 -Compress
            exit 0
        }

        $toDownload = New-Object -ComObject Microsoft.Update.UpdateColl
        $preflightFailures = @($selectionFailures)
        foreach ($update in $applicable) {
            $requiresInput = [bool](Get-SafeValue { $update.InstallationBehavior.CanRequestUserInput } $false)
            if ($requiresInput) {
                $preflightFailures += [pscustomobject]@{
                    updateId = [string]$update.Identity.UpdateID
                    title = [string]$update.Title
                    success = $false
                    warning = $false
                    status = 'Requer interação do instalador do fabricante'
                    errorCode = ''
                    rebootRequired = $false
                }
                continue
            }
            try {
                if (-not $update.EulaAccepted) { $update.AcceptEula() }
                [void]$toDownload.Add($update)
            }
            catch {
                $preflightFailures += [pscustomobject]@{
                    updateId = [string]$update.Identity.UpdateID
                    title = [string]$update.Title
                    success = $false
                    warning = $false
                    status = 'Licença do pacote não pôde ser aceita'
                    errorCode = (Convert-HResult $_.Exception.HResult)
                    rebootRequired = $false
                }
            }
        }

        $downloadResult = $null
        if ($toDownload.Count -gt 0) {
            $downloader = $session.CreateUpdateDownloader()
            $downloader.Updates = $toDownload
            $downloadResult = $downloader.Download()
        }

        $ready = New-Object -ComObject Microsoft.Update.UpdateColl
        $downloadFailures = @()
        for ($index = 0; $index -lt $toDownload.Count; $index++) {
            $update = $toDownload.Item($index)
            if ($update.IsDownloaded) {
                [void]$ready.Add($update)
            }
            else {
                $downloadError = ''
                if ($null -ne $downloadResult) {
                    try { $downloadError = Convert-HResult ($downloadResult.GetUpdateResult($index).HResult) } catch { }
                }
                $downloadFailures += [pscustomobject]@{
                    updateId = [string]$update.Identity.UpdateID
                    title = [string]$update.Title
                    success = $false
                    warning = $false
                    status = 'Falha no download'
                    errorCode = $downloadError
                    rebootRequired = $false
                }
            }
        }

        $installItems = @()
        $overallReboot = $false
        for ($index = 0; $index -lt $ready.Count; $index++) {
            $update = $ready.Item($index)
            $singleUpdate = New-Object -ComObject Microsoft.Update.UpdateColl
            [void]$singleUpdate.Add($update)
            $installer = $session.CreateUpdateInstaller()
            $installer.Updates = $singleUpdate
            $installer.AllowSourcePrompts = $false
            if ($installer.RebootRequiredBeforeInstallation) {
                $installItems += [pscustomobject]@{
                    updateId = [string]$update.Identity.UpdateID
                    title = [string]$update.Title
                    success = $false
                    warning = $false
                    status = 'Reinicie o Windows antes de instalar'
                    errorCode = ''
                    rebootRequired = $true
                }
                $overallReboot = $true
                continue
            }
            try {
                $installResult = $installer.Install()
                $result = $installResult.GetUpdateResult(0)
            }
            catch {
                $installItems += [pscustomobject]@{
                    updateId = [string]$update.Identity.UpdateID
                    title = [string]$update.Title
                    success = $false
                    warning = $false
                    status = 'Falha ao iniciar a instalação'
                    errorCode = (Convert-HResult $_.Exception.HResult)
                    rebootRequired = $false
                }
                continue
            }
            $code = [int]$result.ResultCode
            $hresult = [int64]$result.HResult
            $installItems += [pscustomobject]@{
                updateId = [string]$update.Identity.UpdateID
                title = [string]$update.Title
                success = $code -in @(2, 3)
                warning = $code -eq 3
                status = (Convert-ResultCode $code)
                errorCode = (Convert-HResult $hresult)
                rebootRequired = [bool]$result.RebootRequired
            }
            $overallReboot = $overallReboot -or [bool]$installResult.RebootRequired -or [bool]$result.RebootRequired
        }

        $items = @($preflightFailures) + @($downloadFailures) + @($installItems)
        $installed = @($installItems | Where-Object success).Count
        $warnings = @($installItems | Where-Object warning).Count
        $failed = @($items | Where-Object { -not $_.success }).Count
        [pscustomobject]@{
            found = $requested.Count
            downloaded = $ready.Count
            installed = $installed
            warnings = $warnings
            failed = $failed
            rebootRequired = [bool]$overallReboot
            items = @($items)
        } | ConvertTo-Json -Depth 5 -Compress
        """;
    }
}
