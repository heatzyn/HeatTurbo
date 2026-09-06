# Segurança

Não abra publicamente detalhes de uma vulnerabilidade que permita execução de comandos, elevação de privilégio ou alteração indevida do Windows. Entre em contato diretamente com o responsável pelo repositório pelo perfil do GitHub.

O HeatTurbo executa com privilégios administrativos para criar pontos de restauração. Toda nova operação privilegiada deve usar parâmetros fixos ou validados, registrar falhas com clareza e possuir caminho de restauração.

## Controles atuais

- O servidor interno escuta somente em um endereço de loopback e uma porta aleatória.
- Toda rota `/api` exige um token criptograficamente aleatório, mantido apenas em memória durante a sessão.
- O backend rejeita Host, Origin, Referer e contexto de navegador que não correspondam ao loopback e à porta ativa.
- A interface é carregada somente da pasta instalada. Navegações, frames, downloads e permissões inesperadas do WebView2 são bloqueados.
- A política CSP impede scripts, conexões, objetos e formulários externos.
- IDs de otimização são resolvidos contra uma lista fixa. Seleções de drivers precisam existir na última consulta validada do Windows Update.
- Pontos de restauração são exigidos antes de mudanças e os estados anteriores das otimizações são registrados para reversão.
- Dependências NuGet são auditadas, ações de CI ficam presas a commits e análises CodeQL são executadas automaticamente.

## Limite arquitetural conhecido

Nesta versão, a interface WebView2 ainda compartilha o processo elevado necessário às operações administrativas. Por isso o conteúdo web permanece estritamente local e sem objetos COM expostos. A evolução recomendada é separar a interface sem elevação de um broker administrativo mínimo, autenticado e dedicado.

## Relato responsável

Inclua a versão afetada, impacto, passos mínimos para reprodução e uma forma privada de contato. Não inclua tokens, dados pessoais, dumps completos ou código de exploração em uma issue pública.
