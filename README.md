<div align="center">

# HeatTurbo

### Seu Windows preparado para jogar CS2 — com controle e caminho de volta.

[Baixar o instalador](https://github.com/heatzyn/HeatTurbo/releases/latest/download/HeatTurbo-Setup.exe) · [O que funciona](#-o-que-já-funciona) · [Segurança](#-segurança-primeiro) · [Desenvolvimento](#-desenvolvimento)

</div>

---

O HeatTurbo é um aplicativo desktop para Windows que identifica o hardware, explica cada ajuste e cria proteção antes de mudar o sistema. O projeto ainda está em fase de testes: use em uma máquina de teste ou revise cada opção antes de aplicar.

Versão atual: **0.5.0** · [Ver mudanças](CHANGELOG.md)

## Como baixar e instalar

1. Baixe **[HeatTurbo-Setup.exe na versão mais recente](https://github.com/heatzyn/HeatTurbo/releases/latest/download/HeatTurbo-Setup.exe)**.
2. Execute o instalador e escolha se deseja criar um atalho na área de trabalho.
3. Abra o HeatTurbo e aceite a solicitação do Windows para executar como administrador.

Se você desmarcar “Abrir HeatTurbo” no fim da instalação, também pode iniciá-lo normalmente pelo atalho; o Windows exibirá a confirmação de administrador nesse momento.

Se ainda não houver uma release publicada, use **[Actions → Build Windows app](https://github.com/heatzyn/HeatTurbo/actions/workflows/windows-build.yml)**, abra a execução verde mais recente e baixe o artifact **HeatTurbo-Installer**. O artifact **HeatTurbo-windows-x64** é a versão portátil para testes e desenvolvimento.

O Windows pode mostrar um aviso do SmartScreen enquanto os binários não possuem assinatura digital. Confira se o arquivo veio desta página do repositório. Para distribuição comercial, o instalador deve ser assinado com um certificado de code signing.

## O que já funciona

- Aplicativo em janela própria; nenhum navegador precisa ser aberto.
- Leitura local de CPU, GPU, RAM, disco, placa-mãe, BIOS e Windows.
- Telemetria animada de uso de CPU, GPU 3D, RAM e disco atualizada durante o uso.
- Catálogo com 22 ajustes de jogos, desempenho, latência, energia, rede e interface.
- Modos **Equilibrado** e **Competitivo / CS2**, além da restauração de todos os ajustes de uma vez.
- Estado original de cada ajuste salvo localmente e verificação após aplicar ou restaurar.
- Ponto de restauração automático, criado e confirmado antes da primeira mudança de cada sessão.
- Criação, consulta e restauração para um ponto escolhido diretamente pela aba **Backups**.
- Assistente de BIOS que detecta o hardware e gera recomendações sem gravar firmware.
- Inventário de GPU dedicada/integrada e drivers de chipset, incluindo fornecedor, versão e assinatura.
- Busca, download e instalação dentro do app dos drivers aplicáveis e assinados oferecidos pelo Windows Update, com pareamento por hardware ID e backup prévio.
- Inicialização opcional com o Windows e limpeza automática de temporários antigos.
- Build e instalador automáticos no GitHub Actions.

## 🧭 Primeira utilização

1. Abra **Meu PC** e confirme se o hardware foi identificado corretamente.
2. Abra **Backups** e crie um ponto de restauração manual.
3. Execute a análise no **Painel**.
4. Comece pelo modo **Equilibrado** ou ative somente uma otimização por vez e teste o CS2.
5. Se não gostar, restaure o ajuste, use **Restaurar todos** ou abra **Backups → Restaurar** no ponto criado antes das mudanças. A restauração do sistema reinicia o PC.
6. Em **BIOS**, leia o checklist compatível e altere opções manualmente somente se souber voltar ao padrão.
7. Em **Drivers**, clique em **Verificar drivers**, revise os pacotes encontrados e só então use **Baixar e instalar**.

## 🛡️ Segurança primeiro

O HeatTurbo não desativa Defender, Memory Integrity, paginação do Windows ou Windows Update. Também não remove componentes essenciais e não grava BIOS automaticamente. Essas mudanças aparecem com frequência em “otimizadores”, mas podem causar perda de segurança, travamentos, incompatibilidade com anti-cheat ou impedir a inicialização.

Ao criar um backup, o HeatTurbo tenta habilitar a **Proteção do Sistema**, contorna de forma temporária a janela de 24 horas documentada pelo Windows e só informa sucesso depois de localizar um novo número de sequência. Se isso falhar ou estiver bloqueado por política, a otimização é cancelada. Ponto de restauração protege configurações, drivers e aplicativos; ele não substitui um backup dos arquivos pessoais.

### Como os drivers são obtidos

O HeatTurbo usa a API oficial do **Windows Update Agent**. O Windows compara os IDs do hardware, baixa o pacote aplicável e valida a assinatura antes da instalação. A instalação fica limitada aos mesmos IDs e revisões exibidos para confirmação. BIOS, UEFI, firmware, áudio, rede e armazenamento são excluídos. Essa é a rota de distribuição recomendada pela Microsoft para drivers e evita baixar executáveis por scraping de URLs que mudam.

O catálogo do Windows Update escolhe o melhor pacote aprovado para o equipamento, mas pode não conter no mesmo dia o Game Ready mais recente publicado no NVIDIA App, AMD Software ou Intel DSA. O HeatTurbo não finge contornar essa limitação e não instala silenciosamente pacotes fora de um canal oficial estável.

Documentação técnica: [Windows Update Agent](https://learn.microsoft.com/windows/win32/wua_sdk/searching--downloading--and-installing-updates), [distribuição segura de drivers](https://learn.microsoft.com/windows-hardware/drivers/develop/distributing-a-driver-package) e [seleção por hardware](https://learn.microsoft.com/windows-hardware/drivers/install/how-windows-selects-a-driver-for-a-device).

## Solução de problemas

- **Backup bloqueado:** confirme que o HeatTurbo foi aberto como administrador e que nenhuma política da organização desativou a Restauração do Sistema.
- **Nenhum driver encontrado:** isso significa que o Windows Update não ofereceu um pacote aplicável mais novo; não significa que o hardware não foi detectado.
- **Falha de rede ou política no driver:** o inventário local continua aparecendo e a interface mostra o erro do Windows Update sem instalar nada.
- **Reinício solicitado:** salve o trabalho e reinicie para concluir drivers ou ajustes marcados com reinício.

## 🧑‍💻 Desenvolvimento

Requisitos: Windows 10/11, Visual Studio 2022 ou SDK .NET 8 e WebView2 Runtime.

```powershell
git clone https://github.com/heatzyn/HeatTurbo.git
cd HeatTurbo
dotnet restore
dotnet run
```

Para gerar o aplicativo portátil e, se o Inno Setup estiver instalado, o instalador:

```powershell
.\build-windows.ps1
```

- Aplicativo portátil: `release\win-x64\HeatTurbo.exe`
- Instalador: `release\installer\HeatTurbo-Setup.exe`

Veja [CONTRIBUTING.md](CONTRIBUTING.md) antes de adicionar uma otimização.

## Estado do projeto

Versão inicial para testes. Ganhos de FPS variam conforme hardware, temperatura, drivers, configurações do jogo e processos em segundo plano. Nenhum percentual fixo de desempenho é prometido.
