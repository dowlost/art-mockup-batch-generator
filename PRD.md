# Requisitos do produto

## Objetivo

Gerar, em um aplicativo local, mockups para divulgação de quadros decorativos. A composição aplica a arte com perspectiva e preserva a iluminação da foto do ambiente. O aplicativo trabalha com arquivos locais e usa o FFmpeg para codificar os vídeos.

## Cenários

- O catálogo guarda fotos e configurações em `data/scenes/`.
- A edição usa uma tela com zoom e deslocamento. Cada quadro recebe quatro pontos na ordem superior esquerdo, superior direito, inferior direito e inferior esquerdo.
- As configurações incluem modo de encaixe, iluminação, reflexo, suavização das bordas e uma proporção esperada opcional.
- É possível adicionar, renomear, excluir e conferir a composição de um cenário antes do lote.

## Geração em lote

- **Adicionar imagem** abre o modal na tela inicial. O seletor aceita múltiplas imagens PNG, JPEG e WebP. As artes já existentes também podem ser selecionadas no modal.
- O modal identifica a categoria pela orientação da arte e pela seleção para quadros simples ou duplos. O resumo informa quantas artes, imagens e vídeos estão em cada categoria.
- Cada arte gera um mockup em cada cenário de sua categoria e um vídeo com zoom. A preferência é pelo Cenário 3 do filtro. Se estiver ausente, o modal informa qual cenário da categoria será usado.
- Cenários de dois quadros aceitam uma arte dividida ou um par de artes. É possível ajustar e salvar os cortes, e a geração consegue fazer o corte central em memória.
- O usuário define formato, qualidade, substituição, resolução de vídeo, duração, FPS, intensidade do zoom e retorno à cena inteira. A prévia do vídeo mostra início, meio e fim.
- A geração roda em segundo plano, exibe progresso, permite cancelar, pula os arquivos existentes por padrão e informa erros por item. A saída pode ser aberta em pasta ou baixada em ZIP.

## Impressão

- Exporte a arte inteira ou em duas peças para quadros de 60×90 cm ou 90×60 cm, com borda branca de 0,5 cm de cada lado.
- Escolha posição de recorte, resolução e formato. A ampliação por IA é opcional e depende do Upscayl instalado.

## Critérios de aceite

1. O cenário salvo mantém as coordenadas dos quadros e configurações ao recarregar a aplicação.
2. A prévia posiciona a arte dentro dos quatro cantos e reflete as mudanças de iluminação.
3. Partindo da tela inicial, é possível importar várias artes, filtrar os cenários e gerar imagens e vídeos pelo mesmo modal.
4. Com três cenários e duas artes, o lote prevê seis mockups e dois vídeos.
5. A aplicação registra falhas individuais e mantém as artes de origem intactas durante a geração.
6. A interface, as mensagens da aplicação e a documentação de uso estão em português do Brasil.
