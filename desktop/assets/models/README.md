# Modelo de referência da RTX 3060

O arquivo `../rtx3060.glb` contém 29 peças/grupos com geometria 3D própria,
materiais PBR, normais, dois rotores independentes e pivôs de animação.
`../rtx3060.parts.json` registra os nomes, deslocamentos de explosão, vínculos
de telemetria e SHA-256 das 13 imagens de referência copiadas sem edição.

O projeto editável está em `rtx3060-reference.blend`. O gerador reproduzível
fica em `../../model-tools/build_rtx3060.py`; ele usa uma sessão Blender
isolada com `--background --factory-startup`, sem substituir uma cena aberta.
Os previews montado e explodido são renders da geometria real do arquivo.

A revisão visual de 25/10 refaz os rotores com pás largas curvadas, torção,
pontas arredondadas, oito nervuras moldadas por pá e cubo escalonado. Corrige
o sentido das faces superiores das pás e do lettering lateral, acrescenta
fáscias angulares e trama de carbono geométrica na carenagem. Os 29 IDs,
pivôs e vínculos de telemetria permanecem idênticos. A comparação montada
antes/depois, com a mesma câmera e iluminação, fica em
`../../../evidence/native-refinement-2026-10-25/`.

As dimensões são aproximações visuais em milímetros, sem medição calibrada.
As referências apresentam variações de logotipo, recortes do backplate e
aberturas do espelho; as vistas isoladas das peças foram priorizadas. Os SMDs
representam detalhe visual, não um netlist ou um projeto de fabricação.

Somente o componente GPU possui vínculo térmico localizado. O máximo GPU
não define um ponto medido dentro do die. VRAM, VRM, dissipador, heatpipes e
backplate não recebem temperaturas inventadas; sete canais permanecem sem
localização física comprovada. Uma peça cinza não significa que está fria.

Os índices lógicos 0 e 1 são associados aos rotores visuais por convenção;
essa associação esquerda/direita não foi comprovada fisicamente. A animação
pode acompanhar o RPM reportado, mas não a fase angular real da ventoinha.
O RPM disponibilizado pela NVML é o RPM pretendido informado pelo driver.
