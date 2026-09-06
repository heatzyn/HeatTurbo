# Histórico de versões

## 0.6.1 — hardening de segurança

- Conteúdo do host local preso à pasta instalada, sem depender do diretório de trabalho ou de variáveis de ambiente de desenvolvimento.
- API administrativa limitada ao loopback/porta ativa, token aleatório por sessão, validação de origem e limites rígidos de requisição.
- CSP, bloqueio de framing, política de permissões, `nosniff`, anti-cache da API e remoção do cabeçalho identificador do servidor.
- Ponte COM genérica removida; token entregue por mensagem nativa somente após navegação local validada.
- WebView2 bloqueia frames, downloads, permissões e navegações inesperadas; links externos exigem ação real do usuário.
- Instalação de drivers limitada à seleção exata da última consulta válida e a no máximo 32 pacotes.
- PowerShell, Agendador de Tarefas e `powercfg` resolvidos exclusivamente pelos executáveis protegidos do Windows.
- WebView2 atualizado, bibliotecas web antigas e não utilizadas removidas, NuGet Audit e CodeQL adicionados ao CI.
- GitHub Actions presas a commits imutáveis e permissão de escrita isolada somente no job de release.

## 0.6.0 — drivers internos e perfil competitivo ampliado

- Aba de drivers inicia e diagnostica Windows Update, BITS e Serviços de Criptografia sem abrir navegador.
- Instalação continua limitada aos pacotes assinados de vídeo e chipset aplicáveis ao hardware ID, com BIOS/firmware excluídos.
- Preferência de GPU dedicada aplicada especificamente ao executável do CS2 encontrado nas bibliotecas Steam.
- Política oficial de gravação de jogos e nove controles reversíveis de energia da CPU/rede adicionados.
- Catálogo ampliado para 32 ajustes e novo modo **Desempenho máximo / tomada** com aviso térmico.
- Pesquisa de sugestões da comunidade documentada; tweaks sem suporte, inseguros ou contraditórios continuam excluídos.

## 0.5.0 — recuperação e drivers confiáveis

- Criação de ponto de restauração com ativação da Proteção do Sistema, tentativas controladas e verificação por número de sequência.
- Lista de pontos do Windows e restauração do ponto escolhido diretamente pelo HeatTurbo.
- Inventário separado de GPU dedicada, GPU integrada e componentes de chipset.
- Busca e instalação interna dos drivers assinados de vídeo/chipset oferecidos pelo Windows Update.
- Instalação limitada aos mesmos IDs e revisões que o usuário conferiu na tela; BIOS e firmware permanecem excluídos.
- Dois modos de otimização, 22 ajustes reversíveis, captura do estado original e restauração em lote.
- Telemetria interpolada, correção de uptime e prioridade para a GPU dedicada na identificação.
- Inicialização elevada pelo Agendador de Tarefas, limpeza segura de temporários e instância única do app.
- Link direto para o instalador nas Releases e validações de C# e JavaScript no build do Windows.

> Os recursos que alteram o Windows precisam ser testados em Windows 10/11 como administrador. Ganho fixo de FPS não é prometido.
