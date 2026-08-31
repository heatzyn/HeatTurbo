using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HeatTurbo.Services;

public sealed record RestorePointInfo(
    int SequenceNumber,
    string Description,
    string CreatedAt,
    bool IsHeatTurbo);

public sealed record RestorePointListResult(
    bool Supported,
    bool IsAdministrator,
    bool BlockedByPolicy,
    string SystemDrive,
    string Message,
    IReadOnlyList<RestorePointInfo> Items);

public sealed record RestorePointRestoreRequest(string? Confirmation);

public sealed class RestorePointService
{
    private const string ConfirmationText = "RESTAURAR";
    private const string FrequencyRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyRegistryName = "SystemRestorePointCreationFrequency";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int? _createdSequence;

    private sealed record RegistryValueSnapshot(bool Existed, object? Value, RegistryValueKind Kind);

    public async Task<ActionResult> EnsureBeforeChangeAsync(CancellationToken ct)
    {
        if (_createdSequence is { } sequenceNumber && await PointExistsAsync(sequenceNumber, ct))
            return new(true, $"O ponto de restauração #{sequenceNumber} desta sessão continua disponível.", sequenceNumber.ToString());

        _createdSequence = null;

        return await CreateAsync("HeatTurbo - antes das otimizações", ct);
    }

    public async Task<ActionResult> CreateAsync(string description, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new(false, "Pontos de restauração só estão disponíveis no Windows 10 e 11.");

        await _gate.WaitAsync(ct);
        ActionResult result = new(false, "A criação do ponto de restauração não foi concluída.");
        RegistryValueSnapshot? frequencySnapshot = null;
        try
        {
            frequencySnapshot = SetTemporaryRestorePointFrequency();
            var safeDescription = SanitizeDescription(description);
            var encodedDescription = Convert.ToBase64String(Encoding.Unicode.GetBytes(safeDescription));
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $description = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{encodedDescription}}'))
                $drive = "$env:SystemDrive\"
                $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
                $principal = New-Object Security.Principal.WindowsPrincipal($identity)
                if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                    throw 'O HeatTurbo precisa estar aberto como administrador.'
                }

                $policyPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore'
                $policy = Get-ItemProperty -Path $policyPath -ErrorAction SilentlyContinue
                if ($policy.DisableSR -eq 1) {
                    throw 'A Restauração do Sistema foi desativada por uma política do Windows ou da organização.'
                }

                $class = Get-CimClass -Namespace 'root/default' -ClassName SystemRestore -OperationTimeoutSec 20 -ErrorAction Stop
                $newPoint = $null
                $before = @(
                    Get-CimInstance -Namespace 'root/default' -ClassName SystemRestore -OperationTimeoutSec 20 -ErrorAction SilentlyContinue |
                        ForEach-Object { [int]$_.SequenceNumber }
                )

                function Request-RestorePoint {
                    try {
                        $response = Invoke-CimMethod -CimClass $class -MethodName CreateRestorePoint -Arguments @{
                            Description = $description
                            RestorePointType = [uint32]12
                            EventType = [uint32]100
                        } -OperationTimeoutSec 20 -ErrorAction Stop
                        return [pscustomobject]@{ accepted = [uint32]$response.ReturnValue -eq 0; detail = "código $($response.ReturnValue)" }
                    }
                    catch {
                        return [pscustomobject]@{ accepted = $false; detail = $_.Exception.Message }
                    }
                }

                # Primeiro tente criar: em um volume já protegido não há motivo para aguardar Enable().
                $creation = Request-RestorePoint
                if (-not $creation.accepted) {
                    $enable = Invoke-CimMethod -CimClass $class -MethodName Enable -Arguments @{ Drive = $drive } -OperationTimeoutSec 20 -ErrorAction Stop
                    if ([uint32]$enable.ReturnValue -ne 0) {
                        throw "A criação falhou ($($creation.detail)) e o Windows não conseguiu ativar a Proteção do Sistema (código $($enable.ReturnValue))."
                    }

                    # Enable() retorna antes de a proteção terminar de iniciar. Espere e tente novamente.
                    for ($attempt = 0; $attempt -lt 8 -and -not $creation.accepted; $attempt++) {
                        Start-Sleep -Seconds 3
                        $creation = Request-RestorePoint
                    }
                }
                if (-not $creation.accepted) {
                    throw "A API de Restauração do Sistema não aceitou a criação: $($creation.detail)"
                }

                # A materialização pelo VSS pode levar alguns segundos mesmo depois do retorno zero.
                for ($attempt = 0; $attempt -lt 60 -and $null -eq $newPoint; $attempt++) {
                    Start-Sleep -Milliseconds 500
                    $newPoint = Get-CimInstance -Namespace 'root/default' -ClassName SystemRestore -OperationTimeoutSec 20 -ErrorAction Stop |
                        Where-Object { ($before -notcontains [int]$_.SequenceNumber) -and $_.Description -eq $description } |
                        Sort-Object SequenceNumber -Descending |
                        Select-Object -First 1
                }

                if ($null -eq $newPoint) {
                    throw 'O Windows respondeu sem erro, mas o novo ponto não apareceu na lista. A criação foi cancelada para não indicar um backup inexistente.'
                }
                [pscustomobject]@{
                    sequenceNumber = [int]$newPoint.SequenceNumber
                    description = [string]$newPoint.Description
                } | ConvertTo-Json -Compress
                """;

            var json = await SystemInfoService.RunPowerShellAsync(
                script, CancellationToken.None, TimeSpan.FromMinutes(2));
            using var doc = JsonDocument.Parse(json);
            var sequenceNumber = doc.RootElement.GetProperty("sequenceNumber").GetInt32();
            _createdSequence = sequenceNumber;
            result = new(true, $"Ponto de restauração #{sequenceNumber} criado e verificado com sucesso.", sequenceNumber.ToString());
        }
        catch (Exception ex)
        {
            result = new(false, ExplainFailure(ex, "criar o ponto de restauração"));
        }
        finally
        {
            if (frequencySnapshot is not null)
            {
                try
                {
                    RestoreRestorePointFrequency(frequencySnapshot);
                }
                catch (Exception cleanupError)
                {
                    result = new(false,
                        $"{result.Message} A configuração temporária de frequência não pôde ser restaurada: {cleanupError.Message}",
                        result.Id);
                }
            }
            _gate.Release();
        }
        return result;
    }

    public async Task<RestorePointListResult> GetAllAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new(false, false, false, "—",
                "Pontos de restauração só estão disponíveis no Windows 10 e 11.", []);
        }

        const string script = """
            $ErrorActionPreference = 'Stop'
            $drive = "$env:SystemDrive\"
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            $principal = New-Object Security.Principal.WindowsPrincipal($identity)
            $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            $policyPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore'
            $policy = Get-ItemProperty -Path $policyPath -ErrorAction SilentlyContinue
            $blockedByPolicy = $policy.DisableSR -eq 1
            $null = Get-CimClass -Namespace 'root/default' -ClassName SystemRestore -OperationTimeoutSec 20 -ErrorAction Stop

            function Convert-RestoreTime([object]$value) {
                if ($value -is [DateTime]) { return ([DateTime]$value).ToString('o') }
                try {
                    return [Management.ManagementDateTimeConverter]::ToDateTime([string]$value).ToString('o')
                }
                catch {
                    try { return ([DateTime]::Parse([string]$value)).ToString('o') }
                    catch { return [string]$value }
                }
            }

            $enumerationError = $null
            $items = @()
            try {
                $items = @(
                    Get-CimInstance -Namespace 'root/default' -ClassName SystemRestore -OperationTimeoutSec 20 -ErrorAction Stop |
                        Sort-Object SequenceNumber -Descending |
                        Select-Object -First 25 |
                        ForEach-Object {
                            [pscustomobject]@{
                                sequenceNumber = [int]$_.SequenceNumber
                                description = [string]$_.Description
                                createdAt = (Convert-RestoreTime $_.CreationTime)
                            }
                        }
                )
            }
            catch {
                $enumerationError = $_.Exception.Message
            }

            [pscustomobject]@{
                isAdministrator = [bool]$isAdmin
                blockedByPolicy = [bool]$blockedByPolicy
                systemDrive = $drive
                enumerationError = $enumerationError
                items = $items
            } | ConvertTo-Json -Depth 4 -Compress
            """;

        try
        {
            var json = await SystemInfoService.RunPowerShellAsync(script, ct, TimeSpan.FromSeconds(45));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var isAdministrator = ReadBoolean(root, "isAdministrator");
            var blockedByPolicy = ReadBoolean(root, "blockedByPolicy");
            var systemDrive = ReadString(root, "systemDrive", "C:\\");
            var enumerationError = ReadString(root, "enumerationError", string.Empty);
            var items = new List<RestorePointInfo>();

            if (root.TryGetProperty("items", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    var pointDescription = ReadString(row, "description", "Ponto de restauração");
                    items.Add(new(
                        row.GetProperty("sequenceNumber").GetInt32(),
                        pointDescription,
                        ReadString(row, "createdAt", string.Empty),
                        pointDescription.StartsWith("HeatTurbo", StringComparison.OrdinalIgnoreCase)));
                }
            }

            var message = blockedByPolicy
                ? "A Restauração do Sistema está bloqueada por uma política do Windows ou da organização."
                : !isAdministrator
                    ? "Abra o HeatTurbo como administrador para criar ou restaurar pontos."
                    : !string.IsNullOrWhiteSpace(enumerationError)
                        ? $"O Windows não permitiu consultar os pontos: {enumerationError}"
                        : items.Count == 0
                            ? $"Nenhum ponto encontrado. Ao criar, o HeatTurbo ativará a Proteção do Sistema em {systemDrive}."
                            : $"{items.Count} ponto(s) encontrado(s) em {systemDrive}.";

            return new(true, isAdministrator, blockedByPolicy, systemDrive, message, items);
        }
        catch (Exception ex)
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
            return new(false, false, false, string.IsNullOrWhiteSpace(systemDrive) ? "C:\\" : systemDrive + "\\",
                ExplainFailure(ex, "consultar os pontos de restauração"), []);
        }
    }

    public async Task<ActionResult> RestoreAsync(int sequenceNumber, string? confirmation, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new(false, "A restauração só está disponível no Windows 10 e 11.");
        if (sequenceNumber <= 0)
            return new(false, "O número do ponto de restauração é inválido.");
        if (!string.Equals(confirmation?.Trim(), ConfirmationText, StringComparison.OrdinalIgnoreCase))
            return new(false, $"Confirmação inválida. Digite {ConfirmationText} para continuar.");

        await _gate.WaitAsync(ct);
        try
        {
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
                $principal = New-Object Security.Principal.WindowsPrincipal($identity)
                if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                    throw 'O HeatTurbo precisa estar aberto como administrador.'
                }

                $point = Get-CimInstance -Namespace 'root/default' -ClassName SystemRestore -OperationTimeoutSec 30 -ErrorAction Stop |
                    Where-Object { [int]$_.SequenceNumber -eq {{sequenceNumber}} } |
                    Select-Object -First 1
                if ($null -eq $point) {
                    throw 'O ponto selecionado não existe mais. Atualize a lista e escolha outro ponto.'
                }

                $class = Get-CimClass -Namespace 'root/default' -ClassName SystemRestore -OperationTimeoutSec 30 -ErrorAction Stop
                $restore = Invoke-CimMethod -CimClass $class -MethodName Restore -Arguments @{
                    SequenceNumber = [uint32]{{sequenceNumber}}
                } -OperationTimeoutSec 60 -ErrorAction Stop
                if ([uint32]$restore.ReturnValue -ne 0) {
                    throw "A API de Restauração do Sistema retornou o código $($restore.ReturnValue)."
                }

                $shutdownOutput = & "$env:SystemRoot\System32\shutdown.exe" /r /t 15 /d p:2:4 /c "HeatTurbo: aplicando o ponto de restauração #{{sequenceNumber}}" 2>&1
                $restartScheduled = $LASTEXITCODE -eq 0
                [pscustomobject]@{
                    restartScheduled = [bool]$restartScheduled
                    description = [string]$point.Description
                } | ConvertTo-Json -Compress
                """;

            // Depois que o Windows aceita Restore(), a operação não deve ser interrompida pelo fechamento da página.
            var json = await SystemInfoService.RunPowerShellAsync(
                script, CancellationToken.None, TimeSpan.FromMinutes(3));
            using var doc = JsonDocument.Parse(json);
            var restartScheduled = ReadBoolean(doc.RootElement, "restartScheduled");
            var pointDescription = ReadString(doc.RootElement, "description", $"Ponto #{sequenceNumber}");
            return restartScheduled
                ? new(true, $"Restauração para “{pointDescription}” iniciada. O Windows reiniciará em 15 segundos.", sequenceNumber.ToString())
                : new(true, $"Restauração para “{pointDescription}” preparada. Reinicie o Windows manualmente para concluir.", sequenceNumber.ToString());
        }
        catch (Exception ex)
        {
            return new(false, ExplainFailure(ex, "iniciar a restauração"), sequenceNumber.ToString());
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<bool> PointExistsAsync(int sequenceNumber, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        var script = $$"""
            $exists = $null -ne (Get-CimInstance -Namespace 'root/default' -ClassName SystemRestore -OperationTimeoutSec 15 -ErrorAction Stop |
                Where-Object { [int]$_.SequenceNumber -eq {{sequenceNumber}} } |
                Select-Object -First 1)
            if ($exists) { 'true' } else { 'false' }
            """;
        try
        {
            var value = await SystemInfoService.RunPowerShellAsync(script, ct, TimeSpan.FromSeconds(20));
            return value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static RegistryValueSnapshot SetTemporaryRestorePointFrequency()
    {
        using var key = Registry.LocalMachine.CreateSubKey(FrequencyRegistryPath, writable: true)
            ?? throw new InvalidOperationException("O Registro do Windows não permitiu configurar a frequência dos pontos de restauração.");
        var existed = key.GetValueNames().Contains(FrequencyRegistryName, StringComparer.OrdinalIgnoreCase);
        var snapshot = new RegistryValueSnapshot(
            existed,
            existed ? key.GetValue(FrequencyRegistryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null,
            existed ? key.GetValueKind(FrequencyRegistryName) : RegistryValueKind.Unknown);

        try
        {
            key.SetValue(FrequencyRegistryName, 0, RegistryValueKind.DWord);
            var current = key.GetValue(FrequencyRegistryName);
            if (current is null || Convert.ToInt32(current) != 0)
                throw new InvalidOperationException("O Windows não confirmou a configuração temporária de frequência.");
            return snapshot;
        }
        catch (Exception writeError)
        {
            try
            {
                RestoreRestorePointFrequency(snapshot);
            }
            catch (Exception cleanupError)
            {
                throw new InvalidOperationException(
                    $"Não foi possível preparar nem restaurar a frequência dos pontos: {writeError.Message} / {cleanupError.Message}",
                    writeError);
            }
            throw;
        }
    }

    private static void RestoreRestorePointFrequency(RegistryValueSnapshot snapshot)
    {
        using var key = Registry.LocalMachine.CreateSubKey(FrequencyRegistryPath, writable: true)
            ?? throw new InvalidOperationException("A chave da Restauração do Sistema não pôde ser aberta.");
        if (snapshot.Existed)
        {
            if (snapshot.Value is null)
                throw new InvalidOperationException("O valor original da frequência não pôde ser recuperado.");
            key.SetValue(FrequencyRegistryName, snapshot.Value, snapshot.Kind);
            var current = key.GetValue(FrequencyRegistryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (key.GetValueKind(FrequencyRegistryName) != snapshot.Kind || !Equals(current, snapshot.Value))
                throw new InvalidOperationException("O Windows não confirmou o valor original da frequência.");
        }
        else
        {
            key.DeleteValue(FrequencyRegistryName, throwOnMissingValue: false);
            if (key.GetValueNames().Contains(FrequencyRegistryName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("A configuração temporária de frequência ainda existe.");
        }
    }

    private static string SanitizeDescription(string description)
    {
        var value = new string((description ?? string.Empty)
            .Where(c => !char.IsControl(c))
            .Take(120)
            .ToArray()).Trim();
        return string.IsNullOrWhiteSpace(value) ? "HeatTurbo - backup" : value;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed);

    private static string ReadString(JsonElement element, string propertyName, string fallback) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.ToString()
            : fallback;

    private static string ExplainFailure(Exception exception, string action)
    {
        var detail = exception.Message.Replace("\r", " ").Replace("\n", " ").Trim();
        if (detail.Contains("foi criado, mas a configuração temporária", StringComparison.OrdinalIgnoreCase))
            return detail + " As otimizações foram bloqueadas até você tentar novamente ou revisar essa configuração no Registro.";
        if (detail.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("acesso negado", StringComparison.OrdinalIgnoreCase))
            return $"Não foi possível {action}: acesso negado. Feche e abra o HeatTurbo como administrador.";
        if (detail.Contains("DisableSR", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("política", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("policy", StringComparison.OrdinalIgnoreCase))
            return $"Não foi possível {action}: a Restauração do Sistema está bloqueada por uma política do Windows.";
        if (detail.Contains("0x800423", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("shadow", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("VSS", StringComparison.OrdinalIgnoreCase))
            return $"Não foi possível {action}: o serviço de Cópia de Sombra (VSS) do Windows não está disponível. Verifique os serviços VSS e Microsoft Software Shadow Copy Provider.";

        if (detail.Length > 420) detail = detail[..420] + "…";
        return $"Não foi possível {action}. {detail}";
    }
}
