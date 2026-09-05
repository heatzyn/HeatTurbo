# Pesquisa de desempenho e baixa latência

O HeatTurbo consulta relatos da comunidade para descobrir hipóteses, mas não trata um comentário ou vídeo como prova. Uma opção só entra no catálogo automático quando existe uma interface estável do Windows, o estado anterior pode ser capturado e a restauração pode ser verificada.

## O que entrou na versão 0.6.0

- **GPU dedicada para o CS2:** o Windows oferece uma preferência de GPU de alto desempenho por aplicativo. O HeatTurbo localiza `cs2.exe` nas bibliotecas Steam, preserva o valor anterior e registra somente a preferência desse executável.
- **Resposta da CPU na tomada:** EPP, estado máximo, boost e políticas de subida/queda usam `powercfg`. Cada valor AC/DC original é salvo antes da mudança.
- **Gravação de jogos:** a política `AllowGameDVR` documentada pela Microsoft complementa os controles por usuário já existentes.
- **Drivers:** Windows Update Agent pesquisa, baixa e instala somente pacotes de vídeo/chipset aplicáveis. Windows Update, BITS e CryptSvc são iniciados quando necessário, sem mudar o tipo de inicialização.

## Recomendações que continuam dentro do CS2

- Em GPU NVIDIA compatível, use **NVIDIA Reflex: Ativado**; **Ativado + Boost** prioriza a menor latência com maior consumo e possível pequena perda de FPS.
- Em GPU AMD compatível, use **Radeon Anti-Lag 2** dentro do CS2. É uma integração do próprio jogo e não deve ser simulada por registro.
- Compare média, 1% low, frametime, uso de GPU e temperatura nas mesmas condições. Um limite de FPS pode reduzir latência quando a GPU permanece saturada, mas não existe um número universal para todos os monitores e PCs.

## O que foi rejeitado como padrão

- Desativar Defender, Memory Integrity/VBS, Windows Update ou paginação.
- Forçar HPET, dynamic tick, timer resolution ou comandos `bcdedit` genéricos.
- Desativar núcleos, SMT/E-cores, iGPU ou serviços térmicos sem diagnóstico específico.
- Limpar standby list continuamente, apagar shader cache antes de jogar ou aplicar “debloat” destrutivo.
- Forçar afinidade/prioridade alta, desativar fullscreen optimizations ou adicionar launch options como solução universal.

Esses métodos aparecem em relatos, mas as respostas são contraditórias e vários deles podem piorar stutter, temperatura, segurança ou compatibilidade com anti-cheat. Por isso não fazem parte do perfil automático.

## Fontes primárias

- [Microsoft: referência das configurações de Game Mode e Game Bar](https://learn.microsoft.com/windows/apps/develop/settings/settings-windows-11)
- [Microsoft: gerenciamento de energia e desempenho da CPU](https://learn.microsoft.com/windows-server/administration/performance-tuning/hardware/power/power-performance-tuning)
- [Microsoft: preferência de GPU de alto desempenho](https://learn.microsoft.com/windows/win32/api/dxgi1_6/ne-dxgi1_6-dxgi_gpu_preference)
- [Microsoft: Windows Update Agent](https://learn.microsoft.com/windows/win32/wua_sdk/searching--downloading--and-installing-updates)
- [NVIDIA: Reflex no Counter-Strike 2](https://www.nvidia.com/en-sg/geforce/news/counter-strike-2-released-featuring-nvidia-reflex/)
- [AMD: Radeon Anti-Lag 2](https://www.amd.com/en/products/software/adrenalin/radeon-software-anti-lag.html)

## Discussões da comunidade consultadas

- [Discussão sobre otimização, GPU dedicada e testes por alteração](https://www.reddit.com/r/GlobalOffensive/comments/1r0v7v4/my_cs2_optimization_the_game_runs_terribly_this/)
- [Discussão sobre limite de FPS, saturação da GPU e input lag](https://www.reddit.com/r/GlobalOffensive/comments/16fbfq4/i_tested_the_input_lag_impact_of_every_cs_2/)
- [Discussão mostrando resultados conflitantes de HAGS e outros tweaks](https://www.reddit.com/r/GlobalOffensive/comments/1ckq8fb/every_launch_of_cs2_hits_different/)

Esses links são referências de hipóteses e experiências individuais, não garantia de ganho.
