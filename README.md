<div align="center">

# HeatTurbo

### Seu Windows preparado para jogar CS2 — com controle e caminho de volta.

[Baixar o instalador](#-como-baixar-e-instalar) · [O que funciona](#-o-que-já-funciona) · [Segurança](#-segurança-primeiro) · [Desenvolvimento](#-desenvolvimento)

</div>

---

O HeatTurbo é um aplicativo desktop para Windows que identifica o hardware, explica cada ajuste e cria proteção antes de mudar o sistema. O projeto ainda está em fase de testes: use em uma máquina de teste ou revise cada opção antes de aplicar.

## 📥 Como baixar e instalar

1. Abra a página **[Actions → Build Windows app](https://github.com/heatzyn/HeatTurbo/actions/workflows/windows-build.yml)**.
2. Entre na execução mais recente que tenha um ✅ verde.
3. Role até **Artifacts**.
4. Baixe **`HeatTurbo-Installer`**.
5. Extraia o `.zip` e execute **`HeatTurbo-Setup.exe`**.
6. O instalador cria os atalhos. Abra o HeatTurbo e aceite a solicitação do Windows para executar como administrador.

Se você desmarcar “Abrir HeatTurbo” no fim da instalação, também pode iniciá-lo normalmente pelo atalho; o Windows exibirá a confirmação de administrador nesse momento.

> O instalador fica sempre no artifact **HeatTurbo-Installer**. O artifact **HeatTurbo-windows-x64** é a versão portátil para testes e desenvolvimento.

O Windows pode mostrar um aviso do SmartScreen enquanto os binários não possuem assinatura digital. Confira se o arquivo veio desta página do repositório. Para distribuição comercial, o instalador deve ser assinado com um certificado de code signing.

## ✅ O que já funciona

- Aplicativo em janela própria; nenhum navegador precisa ser aberto.
- Leitura local de CPU, GPU, RAM, disco, placa-mãe, BIOS e Windows.
- Telemetria animada de uso de CPU, GPU 3D, RAM e disco atualizada durante o uso.
- Ajustes reversíveis de Modo de Jogo, Game DVR, mouse e plano de energia.
- Ponto de restauração automático antes da primeira mudança de cada sessão.
- Criação e consulta de pontos de restauração pela aba **Backups**.
- Assistente de BIOS que detecta o hardware e gera recomendações sem gravar firmware.
- Inventário de drivers de vídeo e chipset com links somente para NVIDIA, AMD, Intel e Windows Update.
- Build e instalador automáticos no GitHub Actions.

## 🧭 Primeira utilização

1. Abra **Meu PC** e confirme se o hardware foi identificado corretamente.
2. Abra **Backups** e crie um ponto de restauração manual.
3. Execute a análise no **Painel**.
4. Ative somente uma otimização por vez e teste o CS2.
5. Se não gostar, restaure o ajuste individualmente. Para uma reversão ampla, use **Criar ponto de restauração** no Windows.
6. Em **BIOS**, leia o checklist compatível e altere opções manualmente somente se souber voltar ao padrão.
7. Em **Drivers**, use o botão da fonte oficial correspondente ao componente.

## 🛡️ Segurança primeiro

O HeatTurbo não desativa Defender, Memory Integrity, paginação do Windows ou Windows Update. Também não remove componentes essenciais e não grava BIOS automaticamente. Essas mudanças aparecem com frequência em “otimizadores”, mas podem causar perda de segurança, travamentos, incompatibilidade com anti-cheat ou impedir a inicialização.

Pontos de restauração dependem da **Proteção do Sistema** estar habilitada e o Windows limita a criação pelo `Checkpoint-Computer`. Se o backup falhar, o HeatTurbo bloqueia a aplicação do ajuste e mostra como corrigir.

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
