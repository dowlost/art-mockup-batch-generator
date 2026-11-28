# Gerador de mockups de arte (.NET)

Aplicativo local para aplicar artes aos quadros de fotos de ambientes, gerar mockups e vídeos com zoom e preparar arquivos para impressão. A composição usa perspectiva e iluminação da foto. Consulte os [requisitos do produto](PRD.md).

## Executar

No Windows com o SDK do .NET 10, execute `dotnet run` e abra o endereço exibido no terminal. O padrão fora do perfil de lançamento é `http://localhost:8000`.

Para gerar vídeos, coloque `ffmpeg.exe` na pasta do projeto, instale o FFmpeg no PATH ou defina `ARTMOCKUPS_FFMPEG` com o caminho do executável. Em outros sistemas, use o pacote nativo do OpenCvSharp correspondente ao sistema.

## Gerar em um único modal

1. Na primeira tela, clique em **Adicionar imagem** e selecione várias imagens PNG, JPEG ou WebP do computador. As imagens são copiadas para a pasta de artes configurada, sem sobrescrever arquivos de mesmo nome.
2. O modal identifica a orientação de cada arte pelas dimensões e mostra quantas artes, imagens e vídeos ficam em cada categoria. Selecione os arquivos para quadros duplos na seção separada **Artes para quadros duplos**. O vídeo usa o Cenário 3 de cada categoria quando disponível.
3. Configure as imagens e o vídeo e clique em **Gerar**. O modal oferece JPEG ou PNG, qualidade máxima, substituição, resolução do vídeo, duração, FPS, zoom, retorno à cena inteira e prévia dos três momentos. Você pode acompanhar, cancelar, abrir a pasta de saída ou baixar tudo em ZIP.

A opção **Incluir artes originais sem cenário** vem marcada. O lote inclui uma cópia intacta de cada arte usada com o sufixo `_original` e a extensão de origem. Essas cópias entram no ZIP.

Os arquivos são salvos em `data/output/` como `0001__<cenario>.jpg` e `0001__<cenario>.mp4`. O vídeo que retorna à cena inteira recebe o sufixo `_loop`. Por padrão, arquivos existentes são pulados e falhas aparecem no resumo do lote.

### Cenários duplos

Abra **Cortar imagem** no cabeçalho para dividir uma arte inteira. Escolha a categoria e baixe as partes como PNGs avulsos ou em ZIP. Os nomes `conjunto-esquerda` e `conjunto-direita` identificam um Duplo-vertical. Os nomes `conjunto-cima` e `conjunto-baixo` identificam um Duplo-horizontal. Selecione os dois arquivos em **Adicionar imagem**. Se baixou o ZIP, extraia-o primeiro. O modal gera um conjunto por par, sem dividir as partes novamente, e avisa sobre pares incompletos ou duplicados.

Uma arte horizontal selecionada para duplo é dividida lado a lado e vai para Duplo-vertical. Uma arte vertical selecionada para duplo é dividida em cima e embaixo e vai para Duplo-horizontal. O modal permite ajustar a posição do recorte e salvar as partes `_part1` e `_part2`. Sem partes compatíveis salvas, o gerador faz o corte central em memória. As saídas usam `0001-split__<cenario>` para cortes automáticos e `parte1+parte2__<cenario>` para pares já separados.


### Upscayl portátil no Windows

O modal **Adicionar imagem** tem a seção expansível **Upscayl**. Ative a ampliação para escolher modelo, escala de 1× a 16×, largura em pixels, ampliação dupla, TTA, GPU e tamanho dos blocos. O modal avisa sobre o impacto no desempenho a partir de 5×, com ampliação dupla ou TTA. O formato da imagem final fica nas configurações de imagem do mesmo modal, com PNG, JPEG e WebP. A qualidade do JPEG e do WebP pode ser ajustada na seção Upscayl.

**Arte dentro do quadro** amplia os arquivos usados na composição. **Mockup completo** amplia a cena composta antes da exportação e do zoom. No segundo modo, a opção **Qualidade máxima** evita que o limite de 2 MB reduza as dimensões da imagem de saída. Os vídeos seguem o tamanho configurado no modal. Para refazer resultados existentes, marque **Substituir imagens e vídeos já gerados**. Os originais são preservados e os intermediários reutilizáveis ficam em `data/.cache/upscayl/`.

Em um clone novo, prepare o motor uma vez com `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\preparar-upscayl.ps1`. O script baixa os binários e os sete modelos oficiais da revisão fixada do Upscayl 2.15.0 e monta `tools/upscayl/` sem instalar o aplicativo. Depois do preparo, a ampliação funciona offline e requer GPU e driver compatíveis com Vulkan. `dotnet publish` inclui essa pasta quando ela está preparada. Também é possível transportar a pasta junto com os arquivos do projeto. O pacote inclui a licença e os links das fontes do Upscayl.

## Gerenciar cenários e artes

Na aba **Cenários**, adicione uma foto e marque os quatro cantos de cada quadro. Siga a ordem superior esquerdo, superior direito, inferior direito e inferior esquerdo. Ajuste o encaixe, a luz, o reflexo e as bordas, confira a prévia e salve.

A aba **Biblioteca** permite definir a pasta de artes, conferir miniaturas e numerar novas artes no padrão `0001`. O maior número utilizado é registrado em `data/artworks-counter.json` e o histórico em `data/artworks-rename-map.csv`. Mantenha uma cópia da pasta de artes, que fica fora do Git.

## Arquivos para impressão

A aba **Arquivos para impressão** exporta artes numeradas inteiras ou em duas partes. Cada arquivo inclui uma borda branca de 0,5 cm em todos os lados.

| Disposição | Arte em cada impressão | Tamanho do arquivo |
|---|---|---|
| Inteira | 60×90 cm ou 90×60 cm | 61×91 cm ou 91×61 cm |
| Lado a lado | Dois quadros de 60×90 cm | 61×91 cm por arquivo |
| Um acima do outro | Dois quadros de 90×60 cm | 91×61 cm por arquivo |

As saídas usam os sufixos `_print`, `_print_part1` e `_print_part2`. O PNG preserva a profundidade de 16 bits e o perfil RGB incorporado. Use os pixels originais ou escolha 150, 200 ou 300 DPI para redimensionar. A ampliação opcional usa o mesmo motor portátil e exige GPU compatível com Vulkan. Para apontar a uma instalação existente, defina `ARTMOCKUPS_UPSCAYL` com o caminho da pasta `resources`.

## Estrutura e testes

- `Program.cs` contém a API e serve a interface de `wwwroot/`.
- `Services/` contém a biblioteca, o compositor, o processamento em lote, o vídeo e os arquivos de impressão.
- `data/` contém os cenários, as artes e as saídas. Defina `ARTMOCKUPS_DATA` para usar outra raiz.
- Execute os testes com `dotnet test tests/ArtMockups.Tests`.
