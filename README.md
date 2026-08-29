# HeatTurbo

Otimizador local para Windows focado em CS2, construído em C# e .NET 8.

## O que já funciona

- Detecta CPU, GPU, RAM, disco, Windows, versão e tempo ligado via CIM/PowerShell.
- Analisa o estado do PC sem alterar configurações.
- Ativa e restaura individualmente Modo de Jogo, Game DVR, aceleração do mouse e plano de energia.
- Interface responsiva preta e vermelha; nenhuma informação do hardware é enviada para servidores.
- Continua abrindo em modo de prévia fora do Windows, sem tentar executar ajustes incompatíveis.

## Executar para desenvolvimento

Requer o SDK .NET 8:

```powershell
dotnet restore
dotnet run
```

Abra o endereço local mostrado no terminal. Para testar as otimizações, execute no Windows. Os ajustes atuais são feitos no usuário logado e não exigem privilégios de administrador.

## Gerar o aplicativo para Windows

No PowerShell, dentro da pasta do projeto:

```powershell
.\build-windows.ps1
```

O executável e seus arquivos serão criados em `release\win-x64`. Para distribuir comercialmente, empacote essa pasta com MSIX ou Inno Setup e assine digitalmente o instalador.

## Segurança

O catálogo inicial evita alterações de BIOS, remoção forçada de componentes e promessas fixas de FPS. Ganhos variam por hardware. Antes de adicionar novos ajustes, registre o valor anterior, implemente restauração e teste em versões suportadas do Windows 10/11.
