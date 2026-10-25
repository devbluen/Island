using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Island;

// Catálogo de "partes" dos programas mais usados: biblioteca da Steam, downloads do Chrome, Bluetooth do Windows…
// Cada ação tem palavras-chave em português, inglês e espanhol (sem acento) e um jeito de abrir:
//   uri  → link interno do app (steam://, ms-settings:, discord://…)
//   cmd  → comando do Windows (taskmgr, control, explorer shell:Downloads…)
//   keys → atalho(s) de teclado dentro do app (o app é aberto/focado antes; vários separados por espaço)
//   url  → site (abre no navegador padrão)
//   tab  → endereço interno do navegador (chrome://settings), digitado numa aba nova
//   special → ações do sistema (bloquear, modo escuro, desligar…)
// Para acrescentar algo, basta uma linha nova aqui.
public static class AppCatalog
{
    public sealed record Action(string App, string[] Keywords, string Kind, string Value, string Label);

    // Nomes pelos quais cada app pode ser chamado. "windows" e "web" não precisam ser ditos.
    static readonly Dictionary<string, string[]> AppNames = new()
    {
        ["steam"] = new[] { "steam" },
        ["epic"] = new[] { "epic", "epic games" },
        ["discord"] = new[] { "discord" },
        ["spotify"] = new[] { "spotify" },
        ["browser"] = new[] { "chrome", "google chrome", "navegador", "browser", "edge", "firefox", "opera", "brave", "internet" },
        ["vscode"] = new[] { "vs code", "vscode", "visual studio code", "code" },
        ["explorer"] = new[] { "explorador", "explorer", "explorador de arquivos", "explorador de archivos", "file explorer" },
        ["island"] = new[] { "ilha", "island", "isla" },
        ["windows"] = new[] { "windows", "pc", "computador", "sistema", "computer", "computadora", "ordenador" },
        ["web"] = Array.Empty<string>(),
        ["current"] = Array.Empty<string>(),
    };

    // Processo que recebe os atalhos de teclado de cada app.
    public static readonly Dictionary<string, (string Process, string StartName)> AppProcess = new()
    {
        ["discord"] = ("discord", "Discord"),
        ["spotify"] = ("spotify", "Spotify"),
        ["browser"] = ("navegador", "Google Chrome"),
        ["vscode"] = ("code", "Visual Studio Code"),
        ["explorer"] = ("explorer", "Explorador de Arquivos"),
        ["steam"] = ("steam", "Steam"),
    };

    static Action A(string app, string keywords, string kind, string value, string label) =>
        new(app, keywords.Split('|').Select(Norm).Where(k => k.Length > 0).ToArray(), kind, value, label);

    public static readonly List<Action> Actions = new()
    {
        // ---------------- Steam ----------------
        A("steam", "biblioteca|biblioteca de jogos|meus jogos|library|game library|my games|biblioteca de juegos|mis juegos", "uri", "steam://open/games", "Steam › Biblioteca"),
        A("steam", "loja|store|tienda", "uri", "steam://store", "Steam › Loja"),
        A("steam", "amigos|lista de amigos|friends|friend list|amigos de steam", "uri", "steam://open/friends", "Steam › Amigos"),
        A("steam", "downloads|transferencias|download|descargas", "uri", "steam://open/downloads", "Steam › Downloads"),
        A("steam", "configuracoes|configuracao|ajustes|preferencias|settings|configuracion", "uri", "steam://open/settings", "Steam › Configurações"),
        A("steam", "big picture|modo tv|tv mode|modo televisao", "uri", "steam://open/bigpicture", "Steam › Big Picture"),
        A("steam", "capturas|screenshots|capturas de tela|capturas de pantalla", "uri", "steam://open/screenshots", "Steam › Capturas"),
        A("steam", "lista de desejos|wishlist|lista de deseos", "uri", "steam://openurl/https://store.steampowered.com/wishlist", "Steam › Lista de desejos"),
        A("steam", "inventario|inventory", "uri", "steam://openurl/https://steamcommunity.com/my/inventory", "Steam › Inventário"),
        A("steam", "perfil|meu perfil|profile|my profile|mi perfil", "uri", "steam://openurl/https://steamcommunity.com/my", "Steam › Perfil"),
        A("steam", "comunidade|community|comunidad", "uri", "steam://openurl/https://steamcommunity.com", "Steam › Comunidade"),
        A("steam", "workshop|oficina", "uri", "steam://openurl/https://steamcommunity.com/workshop", "Steam › Workshop"),
        A("steam", "noticias|news", "uri", "steam://open/news", "Steam › Notícias"),

        // ---------------- Epic Games ----------------
        A("epic", "loja|store|tienda", "uri", "com.epicgames.launcher://store", "Epic › Loja"),
        A("epic", "biblioteca|meus jogos|library|my games|biblioteca de juegos", "uri", "com.epicgames.launcher://library", "Epic › Biblioteca"),

        // ---------------- Discord ----------------
        A("discord", "mensagens diretas|mensagens|dm|dms|direct messages|messages|mensajes directos|mensajes|amigos|friends", "uri", "discord://-/channels/@me", "Discord › Mensagens diretas"),
        A("discord", "configuracoes|configuracao|ajustes|settings|configuracion|preferencias", "keys", "ctrl+,", "Discord › Configurações"),
        A("discord", "mutar microfone|mutar o microfone|muta o microfone|muta meu microfone|desmuta o microfone|silenciar microfone|desmutar microfone|mute mic|mute microphone|unmute mic|silenciar microfono", "keys", "ctrl+shift+m", "Discord › Microfone (mudo)"),
        A("discord", "ensurdecer|desensurdecer|deafen|undeafen|ensordecer", "keys", "ctrl+shift+d", "Discord › Ensurdecer"),

        // ---------------- Spotify ----------------
        A("spotify", "musicas curtidas|curtidas|liked songs|liked|canciones que te gustan|me gusta", "uri", "spotify:collection:tracks", "Spotify › Músicas curtidas"),
        A("spotify", "biblioteca|sua biblioteca|minha biblioteca|library|your library|my library|tu biblioteca", "uri", "spotify:collection", "Spotify › Sua biblioteca"),
        A("spotify", "configuracoes|configuracao|preferencias|settings|configuracion", "keys", "ctrl+p", "Spotify › Configurações"),

        // ---------------- Navegador (Chrome, Edge, Firefox…) ----------------
        A("browser", "nova aba|aba nova|nova guia|guia nova|abre uma aba|abre uma guia|new tab|open a new tab|nueva pestana", "keys", "ctrl+t", "Navegador › Nova aba"),
        A("browser", "aba anonima|janela anonima|guia anonima|anonima|incognito|modo anonimo|private window|ventana de incognito|incognito window", "keys", "ctrl+shift+n", "Navegador › Janela anônima"),
        A("browser", "historico|history|historial", "keys", "ctrl+h", "Navegador › Histórico"),
        A("browser", "downloads|transferencias|descargas", "keys", "ctrl+j", "Navegador › Downloads"),
        A("browser", "favoritos|bookmarks|marcadores", "keys", "ctrl+shift+o", "Navegador › Favoritos"),
        A("browser", "fecha a aba|fechar a aba|fechar aba|feche a aba|fecha essa aba|fecha esta aba|fecha a aba atual|fecha a guia|fechar a guia|fechar guia|feche a guia|fecha essa guia|close tab|close the tab|close this tab|cerrar pestana|cierra la pestana|cierra esta pestana", "keys", "ctrl+w", "Navegador › Fechar aba"),
        A("browser", "fecha todas as abas|fechar todas as abas|fecha todas as guias|close all tabs|cierra todas las pestanas", "keys", "ctrl+shift+w", "Navegador › Fechar todas as abas"),
        A("browser", "reabre a aba|reabrir aba|reabrir a aba|reabre a guia|aba fechada|guia fechada|reopen tab|reopen the tab|reopen closed tab|reabrir pestana|reabre la pestana", "keys", "ctrl+shift+t", "Navegador › Reabrir aba"),
        A("browser", "proxima aba|aba seguinte|proxima guia|guia seguinte|next tab|siguiente pestana", "keys", "ctrl+tab", "Navegador › Próxima aba"),
        A("browser", "aba anterior|guia anterior|previous tab|pestana anterior", "keys", "ctrl+shift+tab", "Navegador › Aba anterior"),
        A("browser", "ultima aba|ultima guia|last tab|ultima pestana", "keys", "ctrl+9", "Navegador › Última aba"),
        A("browser", "volta a pagina|voltar a pagina|volta pagina|pagina anterior|volta uma pagina|go back|previous page|vuelve atras|pagina anterior", "keys", "alt+left", "Navegador › Voltar"),
        A("browser", "avanca a pagina|avancar pagina|proxima pagina|go forward|next page|pagina siguiente", "keys", "alt+right", "Navegador › Avançar"),
        A("browser", "recarrega|recarregar|recarrega a pagina|atualiza a pagina|atualizar pagina|atualizar a pagina|reload|refresh|reload the page|recargar|recarga la pagina", "keys", "f5", "Navegador › Recarregar"),
        A("browser", "tela cheia|fullscreen|full screen|pantalla completa", "keys", "f11", "Navegador › Tela cheia"),
        A("browser", "barra de endereco|address bar|barra de direcciones", "keys", "ctrl+l", "Navegador › Barra de endereço"),
        A("browser", "duplica a aba|duplicar aba|duplicate tab|duplicar pestana", "keys", "alt+d alt+enter", "Navegador › Duplicar aba"),
        A("browser", "configuracoes|configuracao|settings|configuracion|ajustes", "tab", "chrome://settings", "Navegador › Configurações"),
        A("browser", "extensoes|extensions|extensiones", "tab", "chrome://extensions", "Navegador › Extensões"),
        A("browser", "senhas|passwords|contrasenas", "tab", "chrome://password-manager/passwords", "Navegador › Senhas"),

        // ---------------- Janela que você está usando (qualquer programa) ----------------
        A("current", "salva|salvar|salve|salva isso|salva o arquivo|save|save it|save the file|guarda|guardar", "keys", "ctrl+s", "Salvar"),
        A("current", "desfaz|desfazer|desfaca|undo|deshacer|deshaz", "keys", "ctrl+z", "Desfazer"),
        A("current", "refaz|refazer|redo|rehacer|rehaz", "keys", "ctrl+y", "Refazer"),
        A("current", "copia|copiar|copie|copy|copiar eso|copia eso", "keys", "ctrl+c", "Copiar"),
        A("current", "cola|colar|cole|paste|pegar", "keys", "ctrl+v", "Colar"),
        A("current", "recorta|recortar|cut", "keys", "ctrl+x", "Recortar"),
        A("current", "seleciona tudo|selecionar tudo|select all|selecciona todo|seleccionar todo", "keys", "ctrl+a", "Selecionar tudo"),
        A("current", "procura na pagina|procurar na pagina|pesquisa na pagina|localiza|localizar|find on page|find on the page|buscar en la pagina", "keys", "ctrl+f", "Procurar"),
        A("current", "imprime|imprimir|imprima|print this|print the page", "keys", "ctrl+p", "Imprimir"),
        A("current", "aumenta o zoom|aumentar o zoom|mais zoom|zoom in|amplia|acercar", "keys", "ctrl+=", "Mais zoom"),
        A("current", "diminui o zoom|diminuir o zoom|menos zoom|zoom out|alejar", "keys", "ctrl+-", "Menos zoom"),
        A("current", "zoom normal|tira o zoom|reset zoom|zoom al 100", "keys", "ctrl+0", "Zoom normal"),
        A("current", "aperta enter|enter|confirma|press enter", "keys", "enter", "Enter"),
        A("current", "aperta esc|esc|escape|cancela isso|press escape", "keys", "esc", "Esc"),

        // ---------------- VS Code ----------------
        A("vscode", "paleta de comandos|command palette|paleta", "keys", "ctrl+shift+p", "VS Code › Paleta de comandos"),
        A("vscode", "configuracoes|configuracao|settings|configuracion", "keys", "ctrl+,", "VS Code › Configurações"),
        A("vscode", "terminal", "keys", "ctrl+`", "VS Code › Terminal"),
        A("vscode", "extensoes|extensions|extensiones", "keys", "ctrl+shift+x", "VS Code › Extensões"),
        A("vscode", "busca|pesquisa global|procurar nos arquivos|search|buscar", "keys", "ctrl+shift+f", "VS Code › Busca"),
        A("vscode", "git|controle de versao|source control|control de codigo", "keys", "ctrl+shift+g", "VS Code › Git"),
        A("vscode", "explorador|arquivos do projeto|explorer|files", "keys", "ctrl+shift+e", "VS Code › Explorador"),
        A("vscode", "nova janela|new window|nueva ventana", "keys", "ctrl+shift+n", "VS Code › Nova janela"),

        // ---------------- Pastas ----------------
        A("explorer", "nova pasta|criar pasta|new folder|nueva carpeta", "keys", "ctrl+shift+n", "Explorador › Nova pasta"),
        A("explorer", "explorador de arquivos|explorador|gerenciador de arquivos|file explorer|explorador de archivos", "cmd", "explorer.exe", "Explorador de Arquivos"),
        A("windows", "downloads|pasta de downloads|transferencias|descargas", "cmd", "explorer.exe shell:Downloads", "Pasta Downloads"),
        A("windows", "documentos|meus documentos|documents|my documents|mis documentos", "cmd", "explorer.exe shell:Personal", "Pasta Documentos"),
        A("windows", "pasta da area de trabalho|desktop folder|carpeta del escritorio", "cmd", "explorer.exe shell:Desktop", "Pasta Área de Trabalho"),
        A("windows", "imagens|pasta de imagens|pictures|imagenes", "cmd", "explorer.exe shell:My Pictures", "Pasta Imagens"),
        A("windows", "pasta de musicas|music folder|carpeta de musica", "cmd", "explorer.exe shell:My Music", "Pasta Músicas"),
        A("windows", "videos|pasta de videos|videos folder", "cmd", "explorer.exe shell:My Video", "Pasta Vídeos"),
        A("windows", "este computador|meu computador|this pc|my computer|este equipo|mi pc", "cmd", "explorer.exe shell:MyComputerFolder", "Este Computador"),
        A("windows", "lixeira|recycle bin|trash|papelera", "cmd", "explorer.exe shell:RecycleBinFolder", "Lixeira"),

        // ---------------- Configurações do Windows ----------------
        A("windows", "configuracoes do windows|configuracoes|settings|windows settings|configuracion|ajustes", "uri", "ms-settings:", "Configurações do Windows"),
        A("windows", "tela|monitor|resolucao|display|screen resolution|pantalla|resolucion", "uri", "ms-settings:display", "Configurações › Tela"),
        A("windows", "luz noturna|night light|luz nocturna|filtro de luz azul", "uri", "ms-settings:nightlight", "Configurações › Luz noturna"),
        A("windows", "configuracoes de som|configuracao de som|sound settings|configuracion de sonido|dispositivos de audio", "uri", "ms-settings:sound", "Configurações › Som"),
        A("windows", "mixer de volume|mixer|volume mixer|mezclador de volumen", "uri", "ms-settings:apps-volume", "Configurações › Mixer de volume"),
        A("windows", "bluetooth|dispositivos bluetooth|bluetooth devices", "uri", "ms-settings:bluetooth", "Configurações › Bluetooth"),
        A("windows", "wifi|wi fi|rede sem fio|wireless|red inalambrica", "uri", "ms-settings:network-wifi", "Configurações › Wi-Fi"),
        A("windows", "configuracoes de rede|rede e internet|network settings|network|configuracion de red", "uri", "ms-settings:network-status", "Configurações › Rede"),
        A("windows", "windows update|atualizacoes|atualizacao do windows|updates|actualizaciones", "uri", "ms-settings:windowsupdate", "Configurações › Windows Update"),
        A("windows", "papel de parede|plano de fundo|wallpaper|background|fondo de pantalla", "uri", "ms-settings:personalization-background", "Configurações › Plano de fundo"),
        A("windows", "cores|cor de destaque|personalizacao|colors|accent color|personalization|colores|personalizacion", "uri", "ms-settings:colors", "Configurações › Cores"),
        A("windows", "temas|themes", "uri", "ms-settings:themes", "Configurações › Temas"),
        A("windows", "tela de bloqueio|lock screen|pantalla de bloqueo", "uri", "ms-settings:lockscreen", "Configurações › Tela de bloqueio"),
        A("windows", "barra de tarefas|taskbar|barra de tareas", "uri", "ms-settings:taskbar", "Configurações › Barra de tarefas"),
        A("windows", "configuracoes de notificacoes|notification settings|configuracion de notificaciones", "uri", "ms-settings:notifications", "Configurações › Notificações"),
        A("windows", "energia|suspensao|plano de energia|power|power and sleep|energia y suspension", "uri", "ms-settings:powersleep", "Configurações › Energia"),
        A("windows", "bateria|economia de bateria|battery|battery saver|ahorro de bateria", "uri", "ms-settings:batterysaver", "Configurações › Bateria"),
        A("windows", "armazenamento|espaco em disco|storage|disk space|almacenamiento", "uri", "ms-settings:storagesense", "Configurações › Armazenamento"),
        A("windows", "aplicativos instalados|desinstalar|programas instalados|installed apps|uninstall|aplicaciones instaladas|desinstalar programas", "uri", "ms-settings:appsfeatures", "Configurações › Aplicativos instalados"),
        A("windows", "aplicativos padrao|apps padrao|default apps|aplicaciones predeterminadas", "uri", "ms-settings:defaultapps", "Configurações › Aplicativos padrão"),
        A("windows", "inicializacao|aplicativos de inicializacao|startup apps|startup|aplicaciones de inicio", "uri", "ms-settings:startupapps", "Configurações › Inicialização"),
        A("windows", "mouse|touchpad|raton", "uri", "ms-settings:mousetouchpad", "Configurações › Mouse"),
        A("windows", "idioma|regiao|teclado|language|region|keyboard layout|idioma y region", "uri", "ms-settings:regionlanguage", "Configurações › Idioma"),
        A("windows", "data e hora|horario|date and time|fecha y hora", "uri", "ms-settings:dateandtime", "Configurações › Data e hora"),
        A("windows", "sobre o pc|sobre o computador|especificacoes|about|about this pc|specs|acerca de", "uri", "ms-settings:about", "Configurações › Sobre"),
        A("windows", "privacidade do microfone|permissao do microfone|microphone privacy|privacidad del microfono", "uri", "ms-settings:privacy-microphone", "Configurações › Microfone (privacidade)"),
        A("windows", "privacidade da camera|permissao da camera|camera privacy|privacidad de la camara", "uri", "ms-settings:privacy-webcam", "Configurações › Câmera (privacidade)"),
        A("windows", "impressoras|printers|impresoras", "uri", "ms-settings:printers", "Configurações › Impressoras"),
        A("windows", "modo de jogo|game mode|modo de juego", "uri", "ms-settings:gaming-gamemode", "Configurações › Modo de jogo"),
        A("windows", "contas|conta|accounts|account|cuentas", "uri", "ms-settings:accounts", "Configurações › Contas"),
        A("windows", "senha do windows|pin|opcoes de entrada|sign in options|password|opciones de inicio de sesion", "uri", "ms-settings:signinoptions", "Configurações › Opções de entrada"),
        A("windows", "seguranca do windows|antivirus|windows security|defender|seguridad de windows", "uri", "windowsdefender:", "Segurança do Windows"),
        A("windows", "microsoft store|loja da microsoft|loja do windows|tienda de microsoft", "uri", "ms-windows-store:", "Microsoft Store"),

        // ---------------- Ferramentas do Windows ----------------
        A("windows", "gerenciador de tarefas|task manager|administrador de tareas", "cmd", "taskmgr.exe", "Gerenciador de Tarefas"),
        A("windows", "painel de controle|control panel|panel de control", "cmd", "control.exe", "Painel de Controle"),
        A("windows", "gerenciador de dispositivos|device manager|administrador de dispositivos", "cmd", "mmc.exe devmgmt.msc", "Gerenciador de Dispositivos"),
        A("windows", "servicos|services|servicios", "cmd", "mmc.exe services.msc", "Serviços"),
        A("windows", "gerenciamento de disco|disk management|administracion de discos", "cmd", "mmc.exe diskmgmt.msc", "Gerenciamento de Disco"),
        A("windows", "limpeza de disco|disk cleanup|liberador de espacio", "cmd", "cleanmgr.exe", "Limpeza de Disco"),
        A("windows", "monitor de recursos|resource monitor", "cmd", "resmon.exe", "Monitor de Recursos"),
        A("windows", "informacoes do sistema|system information|informacion del sistema", "cmd", "msinfo32.exe", "Informações do Sistema"),
        A("windows", "visualizador de eventos|event viewer|visor de eventos", "cmd", "mmc.exe eventvwr.msc", "Visualizador de Eventos"),
        A("windows", "editor de registro|regedit|registry editor", "cmd", "regedit.exe", "Editor de Registro"),
        A("windows", "area de trabalho remota|remote desktop|escritorio remoto", "cmd", "mstsc.exe", "Área de Trabalho Remota"),
        A("windows", "prompt de comando|cmd|command prompt|simbolo del sistema", "cmd", "cmd.exe", "Prompt de Comando"),
        A("windows", "powershell", "cmd", "powershell.exe", "PowerShell"),
        A("windows", "terminal|windows terminal", "cmd", "wt.exe", "Terminal"),
        A("windows", "calculadora|calculator", "cmd", "calc.exe", "Calculadora"),
        A("windows", "bloco de notas|notepad|bloc de notas", "cmd", "notepad.exe", "Bloco de Notas"),
        A("windows", "paint", "cmd", "mspaint.exe", "Paint"),
        A("windows", "relogio do windows|alarmes do windows|cronometro|stopwatch|windows clock|reloj", "uri", "ms-clock:", "Relógio do Windows"),
        A("windows", "fotos|photos", "uri", "ms-photos:", "Fotos"),

        // ---------------- Ações do sistema ----------------
        A("windows", "mostra a area de trabalho|mostrar area de trabalho|minimiza tudo|minimizar tudo|show desktop|minimize all|mostrar escritorio|minimiza todo", "keys", "win+d", "Mostrar área de trabalho"),
        A("windows", "historico da area de transferencia|area de transferencia|clipboard history|clipboard|historial del portapapeles|portapapeles", "keys", "win+v", "Histórico da área de transferência"),
        A("windows", "emoji|emojis|painel de emoji|emoji panel", "keys", "win+.", "Painel de emoji"),
        A("windows", "central de notificacoes|notificacoes|notifications|notification center|centro de notificaciones|notificaciones", "keys", "win+n", "Notificações"),
        A("windows", "configuracoes rapidas|central de acoes|quick settings|action center|configuracion rapida", "keys", "win+a", "Configurações rápidas"),
        A("windows", "visao de tarefas|todas as janelas|task view|vista de tareas", "keys", "win+tab", "Visão de tarefas"),
        A("windows", "game bar|barra de jogo|xbox game bar", "keys", "win+g", "Xbox Game Bar"),
        A("windows", "print|printscreen|captura de tela|recorte|tira um print|screenshot|take a screenshot|captura de pantalla|recortes", "uri", "ms-screenclip:", "Captura de tela"),
        A("windows", "bloquear o pc|bloqueia o pc|bloqueia o computador|bloquear o computador|bloqueia a tela|lock the pc|lock the computer|lock screen now|lock my pc|bloquea el pc|bloquea la computadora", "special", "lock", "Bloquear o PC"),
        A("windows", "modo escuro|tema escuro|dark mode|dark theme|modo oscuro|tema oscuro", "special", "dark", "Modo escuro"),
        A("windows", "modo claro|tema claro|light mode|light theme", "special", "light", "Modo claro"),
        A("windows", "esvazia a lixeira|esvaziar a lixeira|limpa a lixeira|limpar a lixeira|empty the recycle bin|empty recycle bin|empty trash|vacia la papelera|vaciar la papelera", "special", "emptybin", "Esvaziar a lixeira"),
        A("windows", "suspende o pc|suspender o pc|suspende o computador|coloca o pc para dormir|modo de suspensao|put the pc to sleep|put the computer to sleep|suspend the pc|suspender el pc", "special", "sleep", "Suspender o PC"),
        A("windows", "desliga o pc|desligar o pc|desliga o computador|desligar o computador|shut down the pc|shut down the computer|shutdown the pc|turn off the pc|turn off the computer|apaga el pc|apagar la computadora|apaga la computadora", "special", "shutdown", "Desligar o PC"),
        A("windows", "reinicia o pc|reiniciar o pc|reinicia o computador|reiniciar o computador|restart the pc|restart the computer|restart my pc|reboot the pc|reboot the computer|reinicia el pc|reiniciar la computadora", "special", "restart", "Reiniciar o PC"),
        A("windows", "sair da conta|fazer logoff|log off|sign out|cerrar sesion", "special", "logoff", "Sair da conta"),

        // ---------------- A própria ilha ----------------
        A("island", "configuracoes|configuracao|settings|configuracion|ajustes", "special", "islandsettings", "Island › Configurações"),

        // ---------------- Sites (abre o app se estiver instalado) ----------------
        A("web", "youtube", "url", "https://www.youtube.com", "YouTube"),
        A("web", "youtube music", "url", "https://music.youtube.com", "YouTube Music"),
        A("web", "gmail|meu email|meus emails|my email|mi correo", "url", "https://mail.google.com", "Gmail"),
        A("web", "google drive|meu drive|my drive", "url", "https://drive.google.com", "Google Drive"),
        A("web", "google maps|maps|mapa|mapas", "url", "https://maps.google.com", "Google Maps"),
        A("web", "google tradutor|tradutor|google translate|translate|traductor", "url", "https://translate.google.com", "Google Tradutor"),
        A("web", "google agenda|google calendar|calendario|agenda do google", "url", "https://calendar.google.com", "Google Agenda"),
        A("web", "google docs|documentos google", "url", "https://docs.google.com", "Google Docs"),
        A("web", "netflix", "url", "https://www.netflix.com", "Netflix"),
        A("web", "prime video|amazon prime", "url", "https://www.primevideo.com", "Prime Video"),
        A("web", "disney plus|disney", "url", "https://www.disneyplus.com", "Disney+"),
        A("web", "hbo max|hbo", "url", "https://play.max.com", "Max"),
        A("web", "globoplay", "url", "https://globoplay.globo.com", "Globoplay"),
        A("web", "twitch", "url", "https://www.twitch.tv", "Twitch"),
        A("web", "instagram", "url", "https://www.instagram.com", "Instagram"),
        A("web", "facebook", "url", "https://www.facebook.com", "Facebook"),
        A("web", "twitter|abre o x|open x", "url", "https://x.com", "X"),
        A("web", "tiktok", "url", "https://www.tiktok.com", "TikTok"),
        A("web", "linkedin", "url", "https://www.linkedin.com", "LinkedIn"),
        A("web", "reddit", "url", "https://www.reddit.com", "Reddit"),
        A("web", "github", "url", "https://github.com", "GitHub"),
        A("web", "chatgpt", "url", "https://chatgpt.com", "ChatGPT"),
        A("web", "whatsapp web", "url", "https://web.whatsapp.com", "WhatsApp Web"),
        A("web", "outlook|hotmail", "url", "https://outlook.live.com", "Outlook"),
        A("web", "mercado livre|mercadolivre", "url", "https://www.mercadolivre.com.br", "Mercado Livre"),
        A("web", "amazon", "url", "https://www.amazon.com.br", "Amazon"),
        A("web", "shopee", "url", "https://shopee.com.br", "Shopee"),
    };

    static readonly HashSet<string> Generic = new() { "configuracoes", "configuracao", "settings", "configuracion", "ajustes", "preferencias" };

    // Acha a ação mais específica para a frase. null = não é nada do catálogo.
    public static Action? Match(string text)
    {
        var n = " " + Norm(text) + " ";
        Action? best = null;
        int bestScore = int.MinValue;
        foreach (var a in Actions)
        {
            var names = AppNames[a.App];
            bool appSaid = names.Any(name => n.Contains(" " + Norm(name) + " "));
            // Apps específicos precisam ser citados ("a biblioteca DA STEAM"); Windows e sites não.
            // Navegador sem nome ("fecha a aba") vale, mas perde para o Windows quando a palavra é a mesma ("downloads").
            if (!appSaid && a.App is not ("windows" or "web" or "browser" or "current")) continue;
            foreach (var k in a.Keywords)
            {
                if (!n.Contains(" " + k + " ")) continue;
                // Palavras genéricas ("configurações") perdem para as específicas ("configurações de som", "bluetooth").
                // "current" (salvar, copiar…) só quando a frase é praticamente só isso: "copia o arquivo X" é outra coisa.
                if (a.App == "current" && (n.Trim().Split(' ').Length - k.Split(' ').Length > 1 || !n.TrimStart().StartsWith(k))) continue;
                int score = k.Length + (appSaid ? 100 : 0) - (a.App == "browser" && !appSaid ? 5 : 0) - (Generic.Contains(k) ? 20 : 0);
                if (score > bestScore) { best = a; bestScore = score; }
            }
        }
        return best;
    }

    // Lista do que existe para um app (para a IA descobrir).
    public static string Describe(string app)
    {
        var key = AppNames.FirstOrDefault(kv => kv.Value.Any(v => Norm(v) == Norm(app)) || kv.Key == Norm(app)).Key;
        var list = Actions.Where(a => key == null || a.App == key).Select(a => $"- {a.Label}: \"{a.Keywords[0]}\"");
        return string.Join("\n", list.Take(80));
    }

    public static string Norm(string s)
    {
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
