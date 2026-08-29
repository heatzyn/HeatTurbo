# HeatTurbo

Aplicativo desktop para Windows focado em otimização de CS2, construído em C#, .NET 8 e WebView2.

## O que já funciona

- Detecta CPU, GPU, RAM, disco, Windows, versão e tempo ligado via CIM/PowerShell.
- Analisa o estado do PC sem alterar configurações.
- Ativa e restaura individualmente Modo de Jogo, Game DVR, aceleração do mouse e plano de energia.
- Interface responsiva preta e vermelha; nenhuma informação do hardware é enviada para servidores.
- Abre em uma janela desktop própria; o navegador e um endereço localhost não ficam visíveis para o usuário.
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

O aplicativo portátil será criado em `release\win-x64`. Se o Inno Setup estiver instalado, o script também gera `release\installer\HeatTurbo-Setup.exe`, com atalhos, entrada para desinstalação e opção de abrir o app ao terminar.

O Windows 10/11 normalmente já possui o Microsoft Edge WebView2 Runtime. Se ele não estiver presente, o HeatTurbo mostrará uma instrução clara ao iniciar. Antes da distribuição comercial, assine digitalmente o executável e o instalador para reduzir alertas do Windows SmartScreen.

## Segurança

O catálogo inicial evita alterações de BIOS, remoção forçada de componentes e promessas fixas de FPS. Ganhos variam por hardware. Antes de adicionar novos ajustes, registre o valor anterior, implemente restauração e teste em versões suportadas do Windows 10/11.
