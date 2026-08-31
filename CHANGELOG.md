# Histórico de versões

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
