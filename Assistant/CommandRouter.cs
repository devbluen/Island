using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Island;

// Comandos prontos: entende frases comuns direto, sem IA (grátis, offline e instantâneo).
// Se não reconhecer a frase, devolve null e o pedido segue para a IA.
// As frases ficam em Grammar, uma por idioma; o texto chega minúsculo e sem acentos.
public sealed class CommandRouter
{
    readonly IAssistantHost _host;
    readonly AgentTools _tools;

    public CommandRouter(IAssistantHost host, AgentTools tools)
    {
        _host = host;
        _tools = tools;
    }

    sealed record Grammar(
        string PlayPause, string Next, string Previous, string WhatsPlaying,
        string Play, string NotAQuery, string PlaylistPrefix, string AlbumPrefix, string ArtistPrefix, string SongPrefix, string OnSpotify,
        string VolumeSet, string VolumeUp, string VolumeDown, string Mute, string Unmute,
        string Time, string Timer, string Remind1, string Remind2,
        string OpenProject, string BuildProject, string RunProject,
        string Search, string OpenSite, string Close, string Minimize, string Focus, string OpenApp,
        string AppSet, string AppDown, string AppUp, string AppMute, string AppUnmute, string Master,
        string TasksList, string AlarmWake, string AlarmCreate, string TaskRemove, string TaskComplete,
        string AlarmStop, string AlarmSnooze, string QuickOpen, string TasksQuery, string ScrollDown, string ScrollUp, string ScrollTop, string ScrollBottom, string Maximize, string PlayGame)
    {
        const string Domain = @"([a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:com|net|org|io|dev|br|es|mx|ar|app|tv|gg|me)(?:\.br)?(?:/\S*)?)";

        public static readonly Grammar Pt = new(
            PlayPause: @"^(pausa|pausar|pause|para a musica|parar a musica|continua|continuar|play|despausa|solta a musica|volta a tocar)$",
            Next: @"^(proxima|proxima musica|pula|pular|pula a musica|pula essa|passa|passa a musica|next|avanca|toca a proxima|toca a proxima musica)$",
            Previous: @"^(volta a musica|musica anterior|anterior|volta uma musica)$",
            WhatsPlaying: @"^(o que (ta|esta) tocando|que musica e essa|qual (e )?(essa|a) musica)$",
            Play: @"^(?:toca|tocar|toque|coloca|bota|poe|reproduz|reproduzir|quero ouvir|ouvir|escutar|play)(?: pra mim| para mim)? (.+)$",
            NotAQuery: @"^(a |o )?(proxima|anterior|de novo|novamente|musica)$",
            PlaylistPrefix: @"^(?:a |uma )?(?:minha |minhas )?playlists? (?:de |do |da |com |chamada )?",
            AlbumPrefix: @"^(?:o |um )?(?:album|disco) (?:do |da |de )?",
            ArtistPrefix: @"^(?:(?:o |a )?artista |(?:as |os |um |uns |uma |umas )?(?:musicas?|som|sons|cancao|cancoes) d[aoe]s? |(?:alguma coisa|algo) d[aoe]s? )",
            SongPrefix: @"^(?:a |o |uma )?(?:musica |cancao )?",
            OnSpotify: @"\s*\b(?:no|do|pelo|na) (?:meu |minha )?spotify$",
            VolumeSet: @"^(?:coloca |deixa |poe |bota )?(?:o )?volume (?:em |no |para |pra |a )?(\d{1,3})(?: ?%| por cento)?$",
            VolumeUp: @"^(aumenta|sobe|aumentar|subir|mais) (o )?(volume|som)$",
            VolumeDown: @"^(abaixa|diminui|baixa|abaixar|diminuir|menos) (o )?(volume|som)$",
            Mute: @"^(muta|mutar|mudo|silencio|silencia|tira o som)$",
            Unmute: @"^(desmuta|desmutar|tira o mudo|volta o som|liga o som)$",
            Time: @"^(que horas sao|que hora e|me diz as horas|horas)$",
            Timer: @"^(?:inicia|comeca|liga|ativa|bota|coloca|poe)? ?(?:o |um )?(?:timer|pomodoro|modo foco|foco)(?: de (\d+) ?(?:min|minutos?))?$",
            Remind1: @"^(?:me )?lembr(?:a|ar|e)(?: me)? (?:de |que )?(.+)$",
            Remind2: @"^(?:adiciona|anota|cria|coloca)(?: uma)? (?:tarefa|nota|lembrete)(?: de| para| pra)?:? (.+)$",
            OpenProject: @"^(?:abre|abrir|abra)(?: o| a)? (?:projeto|pasta do projeto) (.+?)(?: (?:no|na|com o|com a|com|pelo|pela) (.+))?$",
            BuildProject: @"^(?:compila|compilar|compile|builda|buildar|faz o build d[eao]|faca o build d[eao])(?: o| a)?(?: projeto)? (.+)$",
            RunProject: @"^(?:roda|rodar|executa|executar|inicia|iniciar|starta|liga)(?: o| a)? projeto (.+)$",
            Search: @"^(?:pesquisa|pesquisar|procura|procurar|busca|buscar|googla)(?: por| sobre)? (.+?)(?: no google| na internet| na web)?$",
            OpenSite: @"^(?:abre|abrir|abra|entra|entrar|vai)(?: no| na| o| a| em| pro| pra)? " + Domain + "$",
            Close: @"^(?:fecha|fechar|feche)(?: o| a)? (.{2,40})$",
            Minimize: @"^(?:minimiza|minimizar|minimize|esconde)(?: o| a)? (.{2,40})$",
            Focus: @"^(?:vai para|vai pro|vai pra|mostra|traz|foca)(?: o| a)? (.{2,40})$",
OpenApp: @"^(?:abre|abrir|abra|inicia|iniciar|executa|liga)(?: o| a)? ([a-z0-9 .+#-]{2,30})$",
            AppSet: @"^(?:coloca|coloque|deixa|deixe|poe|ponha|bota|bote|ajusta|ajuste)(?: apenas| so| somente)?(?: o volume| o som| o audio| a musica)? d[oa]s? (.+?) (?:em|no|para|pra|a) (\d{1,3})(?: ?%| por cento)?$",
            AppDown: @"^(?:abaixa|abaixe|diminui|diminua|baixa|baixe)(?: apenas| so| somente)?(?: o volume| o som| o audio| a musica)? d[oa]s? (.+)$",
            AppUp: @"^(?:aumenta|aumente|sobe|suba)(?: apenas| so| somente)?(?: o volume| o som| o audio| a musica)? d[oa]s? (.+)$",
            AppMute: @"^(?:muta|mute|silencia|silencie)(?: o| a)? (.+)$",
            AppUnmute: @"^(?:desmuta|desmute|tira o mudo d[oa]|volta o som d[oa])(?: o| a)? (.+)$",
            Master: @"^(?:meu |o )?(?:computador|pc|sistema|windows|geral|tudo)$",
            TasksList: @"^(?:quais|qual|lista|listar|mostra|mostrar|me diz|me fala|o que|tenho|tem)\b.*\b(?:lembretes?|tarefas?|alarmes?|compromissos?|agenda)\b",
            AlarmWake: @"^(?:me acorda|me acorde|acorda me)\s*(.*)$",
            AlarmCreate: @"^(?:coloca|coloque|cria|crie|bota|bote|poe|ponha|define|defina|liga|programa|programe)?\s*(?:um |o )?(?:alarme|despertador)\b\s*(.*)$",
            TaskRemove: @"^(?:apaga|apague|remove|remova|cancela|cancele|exclui|exclua|tira|tire|deleta|delete)(?: o| a| os| as| todos os| todas as| meus| minhas)? (?:lembretes?|tarefas?|alarmes?)(?: d[aoe]s?| de| que| sobre)?\s*(.*)$",
            TaskComplete: @"^(?:marca|marque|conclui|conclua|terminei|ja fiz|finaliza)(?: como feit[oa])?(?: a| o)?(?: tarefa| lembrete)?\s*(.*?)(?: como feit[oa])?$",
            AlarmStop: @"^(?:para|parar|pare|chega|desliga|desligar|silencio|stop|ok|ja acordei|ja levantei)(?: o)?(?: alarme| despertador)?$",
            AlarmSnooze: @"^(?:soneca|mais (?:5|cinco) minutos|adia|adiar|deixa eu dormir)\b",
            PlayGame: @"^(?:joga|jogar|quero jogar|vamos jogar|bora jogar|bora de|abre o jogo|abrir o jogo|inicia o jogo|roda o jogo)(?: o| a| um| uma)? (.+)$",
            Maximize: @"^(?:maximiza|maximize|maximizar|aumenta a janela d[oa]|deixa em tela cheia [oa])(?: o| a)? (.{2,40})$",
            ScrollDown: @"^(?:rola|role|rolar|escrola|escrole|desce|desca|descer|abaixa|abaixe)(?: (?:pra|para) baixo)?(?: (?:a|essa|esta|nessa) (?:pagina|tela|janela))?(?: (?:pra|para) baixo)?(?: um pouco)?$",
            ScrollUp: @"^(?:sobe|suba|subir|rola|role|escrola|escrole)(?: (?:pra|para) cima)?(?: (?:a|essa|esta) (?:pagina|tela|janela))?(?: (?:pra|para) cima)?(?: um pouco)?$",
            ScrollTop: @"^(?:vai|volta|ir|sobe)(?: (?:pro|para o|ao))? (?:topo|inicio|comeco)(?: d[aoe] (?:pagina|tela))?$",
            ScrollBottom: @"^(?:vai|ir|desce)(?: (?:pro|para o|ao))? (?:final|fim)(?: d[aoe] (?:pagina|tela))?$",
            TasksQuery: @"^(?:(?:o )?que (?:eu )?(?:tenho|tem)|eu tenho (?:algo|alguma coisa|compromisso|reuniao|que fazer)|tenho (?:algo|alguma coisa|compromisso|reuniao|que fazer)|qual (?:e )?(?:a )?minha agenda|como esta (?:a )?minha agenda)\b",
            QuickOpen: @"^(?:abre|abrir|abra|vai|vai para|vai pro|vai no|vai na|entra|entrar|entra no|entra na)(?: no| na| o| a| em)?(?: servidor| canal| conversa| chat| grupo| arquivo| pagina)? ?(.+?) (?:no|do|na|da|pelo|em) (discord|slack|vs ?code|visual studio code|code|cursor|whatsapp|teams|notion)$");

        public static readonly Grammar En = new(
            PlayPause: @"^(pause|resume|play|continue|unpause|stop the music|pause the music)$",
            Next: @"^(next|next song|next track|skip|skip this|skip it|skip song|play the next (one|song))$",
            Previous: @"^(previous|previous song|previous track|go back|last song|play the previous (one|song))$",
            WhatsPlaying: @"^(what'?s playing|what is playing|what song is this|what'?s this song|what is this song)$",
            Play: @"^(?:play|put on|listen to|i want to hear|i want to listen to)(?: me)? (.+)$",
            NotAQuery: @"^(the )?(next|previous|it again|again|music|something)( one| song)?$",
            PlaylistPrefix: @"^(?:the |a |my )?playlists? (?:of |with |called )?",
            AlbumPrefix: @"^(?:the |an )?album ",
            ArtistPrefix: @"^(?:(?:the )?artist |(?:some )?(?:songs|music|something) (?:by|from) |some )",
            SongPrefix: @"^(?:the |a )?(?:song |track )?",
            OnSpotify: @"\s*\b(?:on|from|in) (?:my )?spotify$",
            VolumeSet: @"^(?:set |put |turn )?(?:the )?volume (?:to |at |on )?(\d{1,3})(?: ?%| percent)?$",
            VolumeUp: @"^(turn (it|the volume|the sound) up|volume up|louder|raise (the )?volume|turn up (the )?(volume|sound))$",
            VolumeDown: @"^(turn (it|the volume|the sound) down|volume down|quieter|lower (the )?volume|turn down (the )?(volume|sound))$",
            Mute: @"^(mute|silence|mute (the )?sound)$",
            Unmute: @"^(unmute|sound on|turn the sound (back )?on)$",
            Time: @"^(what time is it|what'?s the time|time|tell me the time)$",
            Timer: @"^(?:start |set )?(?:a |the )?(?:timer|pomodoro|focus timer|focus mode)(?: for (\d+) ?(?:min|minutes?))?$",
            Remind1: @"^remind me (?:to |about |that )?(.+)$",
            Remind2: @"^(?:add|create|note)(?: a| an)? (?:task|note|reminder)(?: to)?:? (.+)$",
            OpenProject: @"^open(?: the| my)? project (.+?)(?: (?:in|with|on|using) (.+))?$",
            BuildProject: @"^(?:build|compile)(?: the| my)?(?: project)? (.+)$",
            RunProject: @"^(?:run|start|launch)(?: the| my)? project (.+)$",
            Search: @"^(?:search|google|look up)(?: for)? (.+?)(?: on google| online| on the web)?$",
            OpenSite: @"^(?:open|go to)(?: the)? " + Domain + "$",
            Close: @"^close(?: the)? (.{2,40})$",
            Minimize: @"^(?:minimize|hide)(?: the)? (.{2,40})$",
            Focus: @"^(?:switch to|go to|show|bring up)(?: the)? (.{2,40})$",
OpenApp: @"^(?:open|launch|start)(?: the)? ([a-z0-9 .+#-]{2,30})$",
            AppSet: @"^(?:set|put|turn)(?: the)?(?: volume| sound| music)?(?: of| for)? (.+?)(?:'s volume| volume)? (?:to|at) (\d{1,3})(?: ?%| percent)?$",
            AppDown: @"^(?:turn down|lower|quiet down|decrease)(?: the)?(?: volume| sound| music)?(?: of| on| for)? (.+)$",
            AppUp: @"^(?:turn up|raise|increase)(?: the)?(?: volume| sound| music)?(?: of| on| for)? (.+)$",
            AppMute: @"^mute(?: the)? (.+)$",
            AppUnmute: @"^unmute(?: the)? (.+)$",
            Master: @"^(?:my |the )?(?:computer|pc|system|windows|everything)$",
            TasksList: @"^(?:what|which|list|show|tell me|do i have)\b.*\b(?:reminders?|tasks?|alarms?|appointments?|agenda|to-?dos?)\b",
            AlarmWake: @"^(?:wake me up|wake me)\s*(.*)$",
            AlarmCreate: @"^(?:set|create|add|put)?\s*(?:an |a |the )?alarm\b\s*(.*)$",
            TaskRemove: @"^(?:delete|remove|cancel|clear)(?: the| my| all| all my| all the)? (?:reminders?|tasks?|alarms?)(?: about| for| called| to)?\s*(.*)$",
            TaskComplete: @"^(?:mark|complete|i did|i finished|done with)(?: the)?(?: task| reminder)?\s*(.*?)(?: as done| as complete)?$",
            AlarmStop: @"^(?:stop|stop it|enough|ok|okay|turn it off|turn off the alarm|i'?m up|i'?m awake)(?: the alarm)?$",
            AlarmSnooze: @"^(?:snooze|five more minutes|5 more minutes)\b",
            PlayGame: @"^(?:let'?s play|i want to play|launch the game|start the game|open the game|run the game) (.+)$",
            Maximize: @"^maximi[sz]e(?: the)? (.{2,40})$",
            ScrollDown: @"^(?:scroll|scroll down|go down|page down)(?: (?:the|this) (?:page|window))?(?: down)?(?: a bit)?$",
            ScrollUp: @"^(?:scroll up|go up|page up)(?: (?:the|this) (?:page|window))?(?: a bit)?$",
            ScrollTop: @"^(?:go|scroll|jump)(?: back)? to the top(?: of the page)?$",
            ScrollBottom: @"^(?:go|scroll|jump) to the (?:bottom|end)(?: of the page)?$",
            TasksQuery: @"^(?:what do i have|what have i got|what'?s on my (?:agenda|calendar|schedule)|do i have (?:anything|something|any)|what am i doing|what'?s planned)\b",
            QuickOpen: @"^(?:open|go to|switch to)(?: the)?(?: server| channel| chat| conversation| group| file| page)? ?(.+?) (?:on|in) (discord|slack|vs ?code|visual studio code|code|cursor|whatsapp|teams|notion)$");

        public static readonly Grammar Es = new(
            PlayPause: @"^(pausa|pausar|pause|continua|continuar|reanuda|reanudar|play|para la musica|detén la musica|deten la musica)$",
            Next: @"^(siguiente|siguiente cancion|salta|saltar|salta esta|pasa|pasa la cancion|proxima|pon la siguiente)$",
            Previous: @"^(anterior|cancion anterior|vuelve|vuelve atras|pon la anterior)$",
            WhatsPlaying: @"^(que (esta sonando|suena|cancion es esta|es esta cancion))$",
            Play: @"^(?:pon|ponme|reproduce|reproducir|toca|quiero escuchar|escuchar|play)(?: me)? (.+)$",
            NotAQuery: @"^(la |el )?(siguiente|anterior|otra vez|de nuevo|musica)$",
            PlaylistPrefix: @"^(?:la |una |mi )?playlists? (?:de |con )?",
            AlbumPrefix: @"^(?:el |un )?(?:album|disco) (?:de )?",
            ArtistPrefix: @"^(?:(?:el |la )?artista |(?:algo|canciones|musica|una cancion) de )",
            SongPrefix: @"^(?:la |una )?(?:cancion )?",
            OnSpotify: @"\s*\b(?:en|de) (?:mi )?spotify$",
            VolumeSet: @"^(?:pon |sube |baja |deja )?(?:el )?volumen (?:a |al |en )?(\d{1,3})(?: ?%| por ciento)?$",
            VolumeUp: @"^(sube|subir|aumenta|aumentar|mas) (el )?(volumen|sonido)$",
            VolumeDown: @"^(baja|bajar|disminuye|disminuir|menos) (el )?(volumen|sonido)$",
            Mute: @"^(silencia|silenciar|silencio|mute|quita el sonido)$",
            Unmute: @"^(quita el silencio|activa el sonido|pon el sonido)$",
            Time: @"^(que hora es|la hora|dime la hora)$",
            Timer: @"^(?:inicia|empieza|pon|activa)? ?(?:el |un )?(?:temporizador|pomodoro|modo enfoque|enfoque)(?: de (\d+) ?(?:min|minutos?))?$",
            Remind1: @"^recuerdame (?:que |de )?(.+)$",
            Remind2: @"^(?:agrega|anade|anota|crea)(?: una)? (?:tarea|nota|recordatorio)(?: de| para)?:? (.+)$",
            OpenProject: @"^(?:abre|abrir)(?: el| mi)? proyecto (.+?)(?: (?:en|con) (.+))?$",
            BuildProject: @"^(?:compila|compilar|construye)(?: el| mi)?(?: proyecto)? (.+)$",
            RunProject: @"^(?:ejecuta|corre|inicia|arranca)(?: el| mi)? proyecto (.+)$",
            Search: @"^(?:busca|buscar|googlea)(?: sobre)? (.+?)(?: en google| en internet)?$",
            OpenSite: @"^(?:abre|ve a|entra en|entra a)(?: la| el)? " + Domain + "$",
            Close: @"^(?:cierra|cerrar)(?: el| la)? (.{2,40})$",
            Minimize: @"^(?:minimiza|minimizar|oculta)(?: el| la)? (.{2,40})$",
            Focus: @"^(?:ve a|muestra|cambia a)(?: el| la)? (.{2,40})$",
OpenApp: @"^(?:abre|abrir|inicia|ejecuta)(?: el| la)? ([a-z0-9 .+#-]{2,30})$",
            AppSet: @"^(?:pon|deja|ajusta)(?: solo| solamente)?(?: el volumen| el sonido| la musica)?(?: de| del) (.+?) (?:a|al|en) (\d{1,3})(?: ?%| por ciento)?$",
            AppDown: @"^(?:baja|bajale|disminuye)(?: solo| solamente)?(?: el volumen| el sonido| la musica)?(?: de| del| a) (.+)$",
            AppUp: @"^(?:sube|subele|aumenta)(?: solo| solamente)?(?: el volumen| el sonido| la musica)?(?: de| del| a) (.+)$",
            AppMute: @"^silencia(?: a| el| la)? (.+)$",
            AppUnmute: @"^(?:quita el silencio|activa el sonido)(?: de| del| a)? (.+)$",
            Master: @"^(?:mi |el |la )?(?:computadora|ordenador|pc|sistema|windows|todo)$",
            TasksList: @"^(?:cuales|que|lista|muestra|dime|tengo)\b.*\b(?:recordatorios?|tareas?|alarmas?|agenda|pendientes)\b",
            AlarmWake: @"^(?:despiertame)\s*(.*)$",
            AlarmCreate: @"^(?:pon|ponme|crea|programa|activa)?\s*(?:una |la )?(?:alarma|despertador)\b\s*(.*)$",
            TaskRemove: @"^(?:borra|elimina|quita|cancela)(?: el| la| los| las| todos los| todas las| mis)? (?:recordatorios?|tareas?|alarmas?)(?: de| sobre| que)?\s*(.*)$",
            TaskComplete: @"^(?:marca|completa|termine|ya hice)(?: como hech[oa])?(?: la| el)?(?: tarea| recordatorio)?\s*(.*?)(?: como hech[oa])?$",
            AlarmStop: @"^(?:para|detente|basta|apaga|apagar|silencio|stop|ok|ya desperte)(?: la)?(?: alarma)?$",
            AlarmSnooze: @"^(?:posponer|pospon|cinco minutos mas|5 minutos mas)\b",
            PlayGame: @"^(?:juega|jugar|quiero jugar|vamos a jugar|abre el juego|inicia el juego)(?: el| la| al| a)? (.+)$",
            Maximize: @"^(?:maximiza|maximizar)(?: el| la)? (.{2,40})$",
            ScrollDown: @"^(?:baja|bajar|desplaza|desplazate|scroll)(?: hacia abajo| abajo)?(?: (?:la|esta) (?:pagina|ventana))?(?: hacia abajo| abajo)?(?: un poco)?$",
            ScrollUp: @"^(?:sube|subir)(?: hacia arriba| arriba)?(?: (?:la|esta) (?:pagina|ventana))?(?: hacia arriba| arriba)?(?: un poco)?$",
            ScrollTop: @"^(?:ve|vuelve|ir) al (?:inicio|principio)(?: de la pagina)?$",
            ScrollBottom: @"^(?:ve|ir|baja) al final(?: de la pagina)?$",
            TasksQuery: @"^(?:que tengo|tengo algo|tengo alguna cosa|que hay en mi agenda|que tengo que hacer|como esta mi agenda)\b",
            QuickOpen: @"^(?:abre|ve a|entra en|entra a)(?: el| la)?(?: servidor| canal| chat| conversacion| grupo| archivo| pagina)? ?(.+?) (?:en|de) (discord|slack|vs ?code|visual studio code|code|cursor|whatsapp|teams|notion)$");

        public static Grammar Current => Loc.Lang switch { "en" => En, "es" => Es, _ => Pt };
    }

    public async Task<string?> TryHandleAsync(string text, bool canFallback, CancellationToken ct)
    {
        var g = Grammar.Current;
        var orig = Clean(text);
        var n = Fold(orig);
        Match m;

        // ---- Alarme tocando: "para" / "soneca" ----
        if (_host.AlarmRinging)
        {
            if (Is(n, g.AlarmSnooze))
            {
                _host.SnoozeAlarm(5);
                return Loc.F("r.snoozed", 5);
            }
            if (Is(n, g.AlarmStop))
            {
                _host.StopAlarm();
                return Loc.T("r.alarmStopped");
            }
        }

        // ---- Atualizações: "procura atualizações", "atualiza a ilha", "check for updates" ("atualiza" sozinho é recarregar a página) ----
        if (Regex.IsMatch(n, @"^(?:atualiza|atualizar|atualize|update|actualiza|actualizar|actualizate)(?: (?:pra|para|to) (?:a )?(?:versao|version) nova)? (?:a ilha|o island|a island|o jarvis|voce|vc|island|yourself|el island|la isla)(?: (?:pra|para|to) (?:a )?(?:versao|version) nova)?(?: (?:agora|now|ahora))?$"))
            return await _host.CheckUpdateAsync(install: true, ct);
        if (Regex.IsMatch(n, @"^(?:procura|procurar|verifica|verificar|ve se tem|tem|check for|look for|any|busca|buscar|hay)(?: (?:se tem|se ha|por))? (?:atualizacao|atualizacoes|versao nova|nova versao|updates?|new version|actualizacion|actualizaciones|version nueva|nueva version)(?: (?:da ilha|do island|da island|do jarvis|for island|disponivel|disponiveis|available))?$"))
            return await _host.CheckUpdateAsync(install: false, ct);

        // ---- Memória: "lembre-se que meu site de anti cheat é xxx.com", "o que você sabe sobre mim?" ----
        if (Is(n, MemListRx)) return MemoryListReply();
        if ((m = Regex.Match(n, MemForgetRx)).Success)
        {
            var output = _tools.Forget(orig.Substring(m.Groups[1].Index, m.Groups[1].Length));
            return output.IsError ? Loc.F("mem.notFound", m.Groups[1].Value) : Loc.F("mem.forgot", output.Text.Replace("Apagado da memória: ", ""));
        }
        if ((m = Regex.Match(n, MemRememberRx)).Success
            || ((m = Regex.Match(n, MemRememberThatRx)).Success && !HasDate(orig.Substring(m.Groups[1].Index))))
        {
            var fact = RawTail(text, orig.Substring(m.Groups[1].Index));
            _tools.Remember(fact);
            return Loc.F("mem.saved", fact);
        }
        // "meu site de anti cheat é esse aqui xxx.com" (afirmação, sem pergunta): aprende.
        if (!text.Contains('?') && UserMemory.Parse(orig) != null && Regex.IsMatch(n, @"^(?:o |a |os |as |the |el |la )?(?:meu|minha|meus|minhas|my|mi|mis) "))
        {
            var fact = RawTail(text, orig);
            _tools.Remember(fact);
            return Loc.F("mem.saved", fact);
        }
        // "abre meu site de anti cheat" → "abre xxx.com": troca pelo que aprendeu e segue.
        var resolved = UserMemory.Resolve(text, _host.Settings.Memories);
        if (resolved != text)
        {
            text = resolved;
            orig = Clean(text);
            n = Fold(orig);
        }

        // ---- Tarefas, lembretes e alarmes ----
        if (Is(n, g.TasksList) || Is(n, g.TasksQuery)) return ListTasksReply(orig);
        if ((m = Rx(n, g.AlarmWake)).Success || (m = Rx(n, g.AlarmCreate)).Success)
            return AddTaskReply(orig.Substring(m.Groups[1].Index, m.Groups[1].Length), alarm: true);
        if ((m = Rx(n, g.TaskRemove)).Success) return RemoveTasksReply(orig.Substring(m.Groups[1].Index, m.Groups[1].Length), complete: false);
        if ((m = Rx(n, g.TaskComplete)).Success && m.Groups[1].Length > 0)
            return RemoveTasksReply(orig.Substring(m.Groups[1].Index, m.Groups[1].Length), complete: true);

        // ---- Volume de um app só: "coloca o Spotify em 20%", "abaixa a música do Spotify" ----
        if ((m = Rx(n, g.AppSet)).Success)
            return AppVolume(m.Groups[1].Value, int.Parse(m.Groups[2].Value), null, null);
        if ((m = Rx(n, g.AppDown)).Success) return AppVolume(m.Groups[1].Value, null, -20, null);
        if ((m = Rx(n, g.AppUp)).Success) return AppVolume(m.Groups[1].Value, null, +20, null);
        if ((m = Rx(n, g.AppUnmute)).Success) return AppVolume(m.Groups[1].Value, null, null, false);
        if ((m = Rx(n, g.AppMute)).Success && !Is(n, g.Mute)) return AppVolume(m.Groups[1].Value, null, null, true);

        // ---- Mouse: "move o mouse para o meio da tela", "clica", "clique com o botão direito" ----
        if (MouseCommand(n) is { } mouse)
        {
            var output = _tools.Mouse(mouse.Action, mouse.Position, null, null, mouse.Dx, mouse.Dy, null);
            return output.IsError ? output.Text : Loc.T("r.ok");
        }

        // ---- WhatsApp: "envie para Vinicius Gomes no WhatsApp: tô chegando" ----
        if (WhatsAppMatch(n) is { } wa)
        {
            // Nome e mensagem recortados da frase com acentos (n é a mesma frase sem acentos, mesmo tamanho).
            var contact = Regex.Replace(orig.Substring(wa.Groups[1].Index, wa.Groups[1].Length).Trim(), @"^(?:o |a |the |el |la )", "");
            var message = orig.Substring(wa.Groups[2].Index).Trim();
            // ...e com as maiúsculas e a pontuação que você falou/digitou.
            int at = text.ToLowerInvariant().LastIndexOf(message, StringComparison.Ordinal);
            if (at >= 0) message = text.Substring(at).TrimEnd();
            int ci = text.ToLowerInvariant().IndexOf(contact, StringComparison.Ordinal);
            if (ci >= 0) contact = text.Substring(ci, contact.Length);
            var output = await _tools.ExecuteAsync("send_whatsapp", Args(("contact", contact), ("message", message)), ct);
            return output.Text;
        }

        // ---- Teclado: "digita bom dia", "aperta control mais c", "pressiona alt f4" ----
        if ((m = Regex.Match(n, @"^(?:digita|digite|digitar|type|teclea|escribe)(?: (?:aqui|ai|pra mim|para mim|for me|o texto|the text|el texto))?:? (.+)$")).Success)
        {
            // Com as maiúsculas originais (a frase foi passada para minúsculas para comparar).
            var typed = orig.Substring(m.Groups[1].Index, m.Groups[1].Length);
            int at = text.ToLowerInvariant().LastIndexOf(typed, StringComparison.Ordinal);
            if (at >= 0) typed = text.Substring(at).TrimEnd(); // até o fim, com a pontuação que você falou
            var output = await _tools.ExecuteAsync("type_text", Args(("text", typed)), ct);
            return output.IsError ? output.Text : Loc.T("r.ok");
        }
        if ((m = Regex.Match(n, @"^(?:aperta|aperte|apertar|pressiona|pressione|press|hit|presiona|pulsa)(?: (?:a tecla|as teclas|o atalho|the key|the keys|the shortcut|la tecla|las teclas|el atajo))? (.+)$")).Success
            && SpokenKeys(m.Groups[1].Value) is { } combo)
        {
            var output = await _tools.ExecuteAsync("press_keys", Args(("keys", combo)), ct);
            return output.IsError ? output.Text : Loc.T("r.ok");
        }

        // ---- Música ----
        // Com direção: "despausa o Spotify", "continua a música", "pausa o YouTube", "dá play"…
        if ((m = Regex.Match(n, MediaResumeRx)).Success || (m = Regex.Match(n, MediaPauseRx)).Success)
        {
            var app = m.Groups["app2"].Success ? m.Groups["app2"].Value : m.Groups["app"].Success ? m.Groups["app"].Value : ""; // "a música DO SPOTIFY": o app nomeado vale
            var mediaAction = Regex.IsMatch(n, MediaResumeRx) ? "play" : "pause";
            return (await _tools.MediaControlAsync(mediaAction, app, ct)).Text;
        }
        if (Is(n, g.PlayPause)) return (await _tools.MediaControlAsync("play_pause", "", ct)).Text;
        if (Is(n, g.Next)) return await Tool("media_control", ("action", "next"), ct) ?? Loc.T("r.nothingPlaying");
        if (Is(n, g.Previous)) return await Tool("media_control", ("action", "previous"), ct) ?? Loc.T("r.nothingPlaying");
        if (Is(n, g.WhatsPlaying))
            return _host.Media.HasMedia ? Loc.F("r.nowPlaying", _host.Media.Title, _host.Media.Artist) : Loc.T("r.nothingPlaying");

        // ---- Spotify: "toca evidências", "coloca a playlist de rock", "play the album X" ----
        if ((m = Rx(n, g.Play)).Success)
        {
            var q = orig.Substring(m.Groups[1].Index, m.Groups[1].Length).Trim();
            var qn = Fold(q);
            // "toca minhas músicas curtidas / favoritas", "play my liked songs", "pon mis favoritas"
            if (IsLiked(Regex.Replace(qn, g.OnSpotify, "", RegexOptions.IgnoreCase).Trim()))
                return await PlaySpotify("", "liked", ct);
            if (!Is(qn, g.NotAQuery))
            {
                string kind = "track";
                if ((m = Rx(qn, g.PlaylistPrefix)).Success) kind = "playlist";
                else if ((m = Rx(qn, g.AlbumPrefix)).Success) kind = "album";
                else if ((m = Rx(qn, g.ArtistPrefix)).Success) kind = "artist";
                else m = Rx(qn, g.SongPrefix);
                q = q[m.Length..];
                q = Regex.Replace(q, g.OnSpotify, "", RegexOptions.IgnoreCase).Trim();
// "toca uma música (no meu Spotify)" / "play some music": continua o que estava tocando.
                if (q.Length == 0 || Regex.IsMatch(Fold(q), @"^(?:algo|alguma coisa|qualquer coisa|uma musica|musica|um som|something|some music|anything|music|algo de musica|una cancion|musica)$"))
                    return await PlaySpotify("", "track", ct);
                return await PlaySpotify(q, kind, ct);
            }
        }

        // ---- Volume ----
        if ((m = Rx(n, g.VolumeSet)).Success)
        {
            int level = Math.Clamp(int.Parse(m.Groups[1].Value), 0, 100);
            await Tool("set_volume", ("level", level), ct);
            return Loc.F("r.volume", level);
        }
        if (Is(n, g.VolumeUp)) return await ChangeVolume(+10, ct);
        if (Is(n, g.VolumeDown)) return await ChangeVolume(-10, ct);
        if (Is(n, g.Mute))
        {
            await Tool("set_volume", ("mute", true), ct);
            return Loc.T("r.muted");
        }
        if (Is(n, g.Unmute))
        {
            await Tool("set_volume", ("mute", false), ct);
            return Loc.T("r.unmuted");
        }

        // ---- Hora ----
        if (Is(n, g.Time)) return Loc.F("r.time", DateTime.Now.ToString(Loc.Lang == "en" ? "h:mm tt" : "HH:mm", Loc.Culture));

        // ---- Timer ----
        if ((m = Rx(n, g.Timer)).Success)
        {
            int? minutes = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : null;
            await Tool("start_focus_timer", ("minutes", minutes), ct);
            return minutes is int x ? Loc.F("r.timerN", x) : Loc.T("r.timer");
        }

        // ---- Lembretes ("me lembra que segunda às 18h tenho reunião") ----
        if ((m = Rx(n, g.Remind1)).Success || (m = Rx(n, g.Remind2)).Success)
            return AddTaskReply(orig.Substring(m.Groups[1].Index, m.Groups[1].Length), alarm: false);

        // ---- Projetos ----
        if ((m = Rx(n, g.OpenProject)).Success)
            return await OpenProject(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null, canFallback, ct);
        if ((m = Rx(n, g.BuildProject)).Success)
            return await BuildProject(m.Groups[1].Value, canFallback, ct);
        if ((m = Rx(n, g.RunProject)).Success)
            return await RunProject(m.Groups[1].Value, canFallback, ct);

        // ---- Internet ----
        if ((m = Rx(n, g.Search)).Success)
        {
            var q = orig.Substring(m.Groups[1].Index, m.Groups[1].Length);
            await Tool("open_url", ("url", "https://www.google.com/search?q=" + Uri.EscapeDataString(q)), ct);
            return Loc.F("r.searching", q);
        }
        if ((m = Rx(n, g.OpenSite)).Success)
        {
            await Tool("open_url", ("url", "https://" + m.Groups[1].Value), ct);
            return Loc.F("r.opening", m.Groups[1].Value);
        }

        // ---- Jogos: "quero jogar CS2", "bora jogar PUBG" ----
        if ((m = Rx(n, g.PlayGame)).Success)
        {
            var output = await _tools.ExecuteAsync("launch_app", Args(("name", m.Groups[1].Value)), ct);
            if (!output.IsError) return Loc.F("r.opening", output.Text.Replace("Aberto: ", "").Replace("Já estava aberto; trouxe para a frente: ", ""));
            if (!canFallback) return output.Text;
        }

        // ---- Rolar a página que você está usando ----
        string? scroll = Is(n, g.ScrollTop) ? "top" : Is(n, g.ScrollBottom) ? "bottom" : Is(n, g.ScrollDown) ? "down" : Is(n, g.ScrollUp) ? "up" : null;
        if (scroll != null)
        {
            var output = await _tools.ExecuteAsync("scroll", Args(("direction", scroll)), ct);
            return output.IsError ? output.Text : Loc.T("r.ok");
        }

        // ---- Dentro de apps: "abre o servidor X no Discord" ----
        if ((m = Rx(n, g.QuickOpen)).Success && AgentTools.QuickOpenApp(m.Groups[2].Value) is { } quickApp)
        {
            var target = orig.Substring(m.Groups[1].Index, m.Groups[1].Length).Trim();
            var output = await _tools.ExecuteAsync("quick_open", Args(("app", quickApp), ("query", target)), ct);
            return output.IsError ? output.Text : Loc.F("r.quickOpen", target, Capitalize(m.Groups[2].Value));
        }

        // ---- Abas do navegador: "fecha a aba do YouTube", "vai para a guia 3", "go to the gmail tab" ----
        if (TabCommand(n) is { } tab)
        {
            var output = await _tools.BrowserTabAsync(tab.Action, tab.Query, tab.Index, ct);
            return output.IsError || tab.Action is "list" or "close_others" or "close_matching" ? output.Text : Loc.T("r.ok");
        }

        // ---- Partes dos programas: "abre a biblioteca da Steam", "abre o bluetooth", "modo escuro" ----
        if (CatalogAction(n) is { } action)
        {
            var output = await _tools.RunCatalogActionAsync(action, ct);
            return output.IsError ? output.Text : Loc.F("r.opening", action.Label);
        }

        // ---- Janelas ----
        if ((m = Rx(n, g.Close)).Success) return await WindowAction(m.Groups[1].Value, "close", canFallback, ct);
        if ((m = Rx(n, g.Minimize)).Success) return await WindowAction(m.Groups[1].Value, "minimize", canFallback, ct);
        if ((m = Rx(n, g.Focus)).Success) return await WindowAction(m.Groups[1].Value, "focus", canFallback, ct);
        if ((m = Rx(n, g.Maximize)).Success) return await WindowAction(m.Groups[1].Value, "maximize", canFallback, ct);

        // ---- Apps ----
        if ((m = Rx(n, g.OpenApp)).Success && m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3)
        {
            var output = await _tools.ExecuteAsync("launch_app", Args(("name", m.Groups[1].Value)), ct);
            if (!output.IsError) return Loc.F("r.opening", output.Text.Replace("Aberto: ", "").Replace("Já estava aberto; trouxe para a frente: ", ""));
            return canFallback ? null : output.Text;
        }

        return null;
    }

    static readonly Regex[] WhatsAppPatterns =
    {
        // "envie para Vinicius Gomes no WhatsApp. Essa mensagem…" / "manda uma mensagem pro João no zap: tô chegando"
        new(@"^(?:envia|envie|enviar|manda|mande|mandar|escreve|escreva)(?: uma| a)?(?: mensagem)?(?: (?:para|pra|pro|ao|a|à)) (.+?) (?:no|pelo|via) (?:whatsapp|whats|zap|zapzap)[.,:;!]? (?:dizendo |falando |que |escrito |com a mensagem |a mensagem |assim )?(.+)$"),
        new(@"^(?:envia|envie|enviar|manda|mande|mandar)(?: uma| a)?(?: mensagem)? (?:no|pelo|via) (?:whatsapp|whats|zap|zapzap) (?:para|pra|pro|ao|a) (.+?)[.,:;!] (?:dizendo |falando |que )?(.+)$"),
        new(@"^(?:send|text|message)(?: a message)?(?: to)? (.+?) (?:on|via) whatsapp[.,:;!]? (?:saying |that )?(.+)$"),
        new(@"^(?:envia|envie|manda|mande)(?: un mensaje)? (?:a|para) (.+?) (?:en|por) whatsapp[.,:;!]? (?:diciendo |que )?(.+)$"),
    };

    static Match? WhatsAppMatch(string n)
    {
        foreach (var rx in WhatsAppPatterns)
        {
            var m = rx.Match(n);
            if (m.Success && m.Groups[2].Value.Trim().Length > 0) return m;
        }
        return null;
    }

    // "control mais shift mais t" → "ctrl+shift+t"; null se não for um atalho reconhecível.
    static string? SpokenKeys(string spoken)
    {
        // Só o artigo do começo ("aperta O enter"); um "a" no meio é a letra A ("control mais a").
        var s = " " + Regex.Replace(spoken.Trim(), @"^(?:a|o|as|os|the|la|el|las|los) ", "") + " ";
        foreach (var (from, to) in new[]
        {
            ("seta para cima", "up"), ("seta pra cima", "up"), ("seta para baixo", "down"), ("seta pra baixo", "down"),
            ("seta para a esquerda", "left"), ("seta para esquerda", "left"), ("seta para a direita", "right"), ("seta para direita", "right"),
            ("arrow up", "up"), ("up arrow", "up"), ("down arrow", "down"), ("left arrow", "left"), ("right arrow", "right"),
            ("barra de espaco", "space"), ("page down", "pagedown"), ("page up", "pageup"), ("print screen", "printscreen"),
        })
            s = s.Replace(" " + from + " ", " " + to + " ");
        var parts = Regex.Split(s.Trim(), @"\s*(?:\+|\bmais\b|\bplus\b|\bmas\b|\bcom\b|\bwith\b|\bcon\b|\be\b|\band\b|\by\b)\s*|\s+")
            .Where(p => p.Length > 0 && p is not ("tecla" or "teclas" or "key" or "keys"));
        var keys = new List<string>();
        foreach (var raw in parts)
        {
            var k = raw switch
            {
                "control" or "controle" or "ctrl" or "contrl" => "ctrl",
                "shift" or "xift" or "chift" => "shift",
                "alt" or "aut" => "alt",
                "windows" or "win" or "janela" => "win",
                "enter" or "entrar" or "intro" or "return" => "enter",
                "esc" or "escape" or "escap" => "esc",
                "espaco" or "space" or "espacio" => "space",
                "tab" or "tabe" => "tab",
                "delete" or "del" or "deletar" or "suprimir" or "supr" => "delete",
                "backspace" or "apagar" or "retroceso" => "backspace",
                "cima" or "up" or "arriba" => "up",
                "baixo" or "down" or "abajo" => "down",
                "esquerda" or "left" or "izquierda" => "left",
                "direita" or "right" or "derecha" => "right",
                "home" or "inicio" => "home",
                "end" or "fim" or "fin" => "end",
                "pagedown" or "pageup" or "printscreen" => raw,
                "virgula" or "comma" or "coma" => ",",
                "ponto" or "period" or "punto" => ".",
                _ when Regex.IsMatch(raw, @"^f(?:[1-9]|1[0-2])$") => raw,
                _ when raw.Length == 1 && char.IsLetterOrDigit(raw[0]) => raw,
                _ => null,
            };
            if (k == null) return null;
            keys.Add(k);
        }
        return keys.Count is > 0 and <= 4 ? string.Join("+", keys) : null;
    }

    static (string Action, string Position, int? Dx, int? Dy)? MouseCommand(string n)
    {
        // Cliques
        if (Regex.IsMatch(n, @"^(?:da (?:um )?)?(?:duplo clique|clique duplo|clica duas vezes|clique duas vezes|double click|double-click|doble clic|doble click)(?: aqui| ai| here)?$")) return ("double_click", "", null, null);
        if (Regex.IsMatch(n, @"^(?:da (?:um )?)?(?:clique direito|clica com o (?:botao )?direito|clique com o (?:botao )?direito|botao direito|right click|right-click|clic derecho|haz clic derecho|clic con el boton derecho)(?: aqui| ai| here)?$")) return ("right_click", "", null, null);
        if (Regex.IsMatch(n, @"^(?:da (?:um )?)?(?:clica|clique|clicar|um clique|click|click here|click it|haz clic|haz click|clic)(?: aqui| ai| nisso| here)?$")) return ("click", "", null, null);

        // Mover: "move o mouse para o meio da tela", "move the mouse to the top right", "mueve el ratón al centro"
        var m = Regex.Match(n, @"^(?:move|mova|mover|leva|coloca|coloque|bota|poe|joga|manda|mueve|lleva|pon)(?: (?:o|a|the|el))?(?: (?:meu|my|mi))? (?:mouse|cursor|ponteiro|setinha|raton|pointer)(?: (?:para|pro|pra|no|na|ao|ate|to|towards|al|a|hacia|en))?(?: (?:o|a|the|el|la))? ?(.+)$");
        if (!m.Success) return null;
        var rest = m.Groups[1].Value;

        // "200 pixels para a direita" / "100 px up"
        var rel = Regex.Match(rest, @"^(\d+) ?(?:pixels?|px)?(?: (?:para|pra|pro|to|hacia))?(?: (?:a|o|the|la))? ?(direita|esquerda|cima|baixo|right|left|up|down|derecha|izquierda|arriba|abajo)");
        if (rel.Success)
        {
            int d = int.Parse(rel.Groups[1].Value);
            return rel.Groups[2].Value switch
            {
                "direita" or "right" or "derecha" => ("move", "", d, null),
                "esquerda" or "left" or "izquierda" => ("move", "", -d, null),
                "cima" or "up" or "arriba" => ("move", "", null, -d),
                _ => ("move", "", null, d),
            };
        }

        bool top = Regex.IsMatch(rest, @"\b(?:superior|cima|topo|alto|top|upper|arriba)\b");
        bool bottom = Regex.IsMatch(rest, @"\b(?:inferior|baixo|embaixo|bottom|lower|abajo)\b");
        bool left = Regex.IsMatch(rest, @"\b(?:esquerd[oa]|left|izquierd[oa])\b");
        bool right = Regex.IsMatch(rest, @"\b(?:direit[oa]|right|derech[oa])\b");
        bool center = Regex.IsMatch(rest, @"\b(?:meio|centro|center|centre|middle|medio)\b");
        var position = top && left ? "top_left" : top && right ? "top_right" : bottom && left ? "bottom_left" : bottom && right ? "bottom_right"
            : top ? "top" : bottom ? "bottom" : left ? "left" : right ? "right" : center ? "center" : "";
        return position.Length > 0 ? ("move", position, null, null) : null;
    }

    static readonly (string Action, Regex Rx)[] TabPatterns =
    {
        ("close", new(@"^(?:fecha|fechar|feche|fecho)(?: a| essa| esta)? (?:aba|guia)(?: d[oa]| com o| com a| que tem o| que tem a| que tem| de)? (.+)$")),
        ("close", new(@"^close(?: the)? (.+?) tab$")),
        ("close", new(@"^close the tab (?:with|of|for) (?:the )?(.+)$")),
        ("close", new(@"^(?:cierra|cerrar)(?: la)? pestana (?:de|del|con) (?:la |el )?(.+)$")),
        ("switch", new(@"^(?:vai|vai para|vai pra|vai pro|volta para|volta pra|muda para|muda pra|troca para|troca pra|abre|mostra|entra)(?: a| na| no)? (?:aba|guia)(?: d[oa]| de| com o| com a| que tem o| que tem a| que tem)? (.+)$")),
        ("switch", new(@"^(?:go to|switch to|open)(?: the)? (.+?) tab$")),
        ("switch", new(@"^(?:go to|switch to)(?: the)? tab (?:with|of|for)? ?(?:the )?(.+)$")),
        ("switch", new(@"^(?:ve a|ir a|cambia a|abre)(?: la)? pestana (?:de|del|con)? ?(?:la |el )?(.+)$")),
    };

    static readonly Dictionary<string, int> TabNumbers = new()
    {
        ["um"] = 1, ["uma"] = 1, ["primeira"] = 1, ["one"] = 1, ["first"] = 1, ["uno"] = 1, ["primera"] = 1,
        ["dois"] = 2, ["duas"] = 2, ["segunda"] = 2, ["two"] = 2, ["second"] = 2, ["dos"] = 2,
        ["tres"] = 3, ["terceira"] = 3, ["three"] = 3, ["third"] = 3, ["tercera"] = 3,
        ["quatro"] = 4, ["quarta"] = 4, ["four"] = 4, ["fourth"] = 4, ["cuatro"] = 4, ["cuarta"] = 4,
        ["cinco"] = 5, ["quinta"] = 5, ["five"] = 5, ["fifth"] = 5,
        ["seis"] = 6, ["sexta"] = 6, ["six"] = 6, ["sete"] = 7, ["seven"] = 7, ["siete"] = 7,
        ["oito"] = 8, ["eight"] = 8, ["ocho"] = 8, ["ultima"] = 9, ["last"] = 9,
    };

    const string Polite = @"^(?:(?:voce )?(?:poderia|pode|consegue|could you|can you|would you|puedes|podrias) )?";

    // Várias abas de uma vez: "fecha todas as guias e deixa só o Instagram e a apuração", "quais abas estão abertas?"
    static readonly (string Action, Regex Rx)[] MultiTabPatterns =
    {
        ("close_others", new(Polite + @"(?:fecha|fechar|feche|close|cierra|cerrar)(?: todas)?(?: as| all| the| las)?(?: outras| other| otras)? (?:abas|guias|tabs|pestanas)(?: do navegador| do chrome| in the browser| del navegador)?,?(?: (?:e|and|y))? (?:menos|exceto|tirando|fora|except|but|apart from|other than|deixa|deixar|deixando|deixe|leave|leaving|keep|keeping|dejando|deja|salvo)(?: (?:apenas|so|somente|only|solo|aberta|abertas|open))*(?: (?:a|as|o|os|the|la|las))? (.+?)(?: abert[oa]s?| open| abiert[oa]s?)?$")),
        ("close_others", new(Polite + @"(?:deixa|deixe|deixar|leave|keep|deja|dejar) (?:apenas|so|somente|only|solo)(?: (?:a|as|o|os|the|la|las))?(?: (?:aba|abas|guia|guias|tab|tabs|pestana|pestanas))?(?: (?:do|da|dos|das|of|de))? (.+?)(?: abert[oa]s?| open| abiert[oa]s?)?$")),
        ("close_matching", new(Polite + @"(?:fecha|fechar|feche|close|cierra|cerrar)(?: todas)?(?: (?:as|all the|all|las|the))? (?:abas|guias|tabs|pestanas) (?:do|da|dos|das|com|de|del|of|with) (?:o |a |the |el |la )?(.+)$")),
        ("list", new(@"^(?:quais|que|which|what|cuales) (?:as )?(?:abas|guias|tabs|pestanas)(?: (?:estao|tenho|are|tengo|hay))?(?: (?:abertas?|open|abiertas?))?(?: no navegador| in the browser)?$")),
        ("list", new(@"^(?:lista|listar|liste|mostra|list|show me|show) (?:as |the |las )?(?:abas|guias|tabs|pestanas)(?: abertas| open| abiertas)?$")),
    };

    static (string Action, string Query, int? Index)? TabCommand(string n)
    {
        foreach (var (action, rx) in MultiTabPatterns)
        {
            var mm = rx.Match(n);
            if (mm.Success) return (action, mm.Groups.Count > 1 ? mm.Groups[1].Value.Trim() : "", null);
        }
        foreach (var (action, rx) in TabPatterns)
        {
            var m = rx.Match(n);
            if (!m.Success) continue;
            var q = m.Groups[1].Value.Trim();
            // "fecha a aba do chrome" = a aba atual do Chrome; "fecha a aba atual" também.
            if (Regex.IsMatch(q, @"^(?:o |a |the |el |la )?(?:chrome|google chrome|edge|firefox|opera|brave|navegador|browser|atual|current|actual)$")) q = "";
            if (q.Length == 0 && action == "switch") return null;
            // "abre a aba anônima", "vai para a próxima aba": são ações do catálogo, não nomes de aba.
            if (Regex.IsMatch(q, @"^(?:anonima|incognito|nova|novas|anterior|seguinte|fechada|privada|new|private|nueva)\b")) return null;
            int? index = null;
            if (int.TryParse(q, out var num)) index = num;
            else if (TabNumbers.TryGetValue(q, out var w)) index = w;
            return (action, index != null ? "" : q, index);
        }
        return null;
    }

    static bool IsLiked(string q) => Regex.IsMatch(q,
        @"^(?:a |as |o |os |the |la |las |mis? |my )?(?:minhas? |meus? |my |mis )?(?:playlist (?:de |das |of |the )?|lista (?:de )?)?(?:musicas? |cancoes |songs? |tracks |canciones |temas )?(?:curtidas?|favoritas?|preferidas?|salvas|que eu curti|que curti|liked(?: songs)?|favou?rites?|favorite songs|saved songs|que me gustan|me gusta)(?: (?:musicas|songs|canciones))?$")
        || Regex.IsMatch(q, @"^(?:minhas musicas|my music|my songs|mis canciones|minha biblioteca|my library)$");

    // Verbos que indicam "abrir/mostrar/ligar algo" nos três idiomas.
    const string CatalogVerb = @"^(?:(?:me |pode |por favor )?(?:abre|abrir|abra|abri|mostra|mostrar|mostre|vai|vai para|vai pro|vai pra|vai na|vai no|ir para|entra|entrar|entra na|entra no|ativa|ativar|ative|liga|ligar|coloca|coloque|bota|deixa|deixe|muda para|muda pro|troca para|poe|faz|faca|executa|inicia|open|show|show me|go to|take me to|switch to|launch|start|turn on|enable|put|set|muestra|muestrame|ve a|ir a|activa|pon|cambia a)\b|(?:i want to see|quero ver|quiero ver)\b)";

    // Só quando a frase é curta e é um pedido de abrir/fazer — ou começa pela própria ação ("bloqueia o pc").
    static AppCatalog.Action? CatalogAction(string n)
    {
        if (n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 10) return null;
        var a = AppCatalog.Match(n);
        if (a == null) return null;
        if (Regex.IsMatch(n, CatalogVerb)) return a;
        var start = AppCatalog.Norm(n);
        return a.Kind != "url" && a.Keywords.Any(k => start.StartsWith(k)) ? a : null;
    }

    // ---------------- Mídia ----------------

    const string MediaApp = @"(?:(?: (?:o|a|the|el|la))? (?<app>spotify|youtube|deezer|navegador|chrome|musica|som|video|player|music|song|cancion|reproductor))?(?: (?:do|no|da|on|in|en|del) (?<app2>spotify|youtube|deezer|navegador|chrome))?(?: (?:pra mim|para mim|por favor|please|por fa))?$";
    const string MediaResumeRx = @"^(?:despausa|despausar|despause|despausa ai|tira (?:do |o )?pause|continua|continuar|continue|volta a tocar|voltar a tocar|retoma|retomar|da (?:um )?play|dar play|da o play|aperta o play|play|toca de novo|resume|unpause|keep playing|reanuda|reanudar|sigue|quita la pausa)" + MediaApp;
    const string MediaPauseRx = @"^(?:pausa|pausar|pause|pausa ai|pare|para|parar|da (?:um )?pause|dar pause|da o pause|aperta o pause|stop|deten|detener)" + MediaApp;

    // ---------------- Memória ----------------

    const string MemListRx = @"^(?:o que (?:voce|vc) (?:sabe|aprendeu|lembra|guardou) (?:sobre|de) mim|o que (?:tem|ha) na (?:sua )?memoria|what do you know about me|what have you learned about me|what do you remember about me|que sabes (?:de|sobre) mi|que recuerdas de mi)$";
    const string MemForgetRx = @"^(?:esquece|esqueca|esquecer|forget|olvida|olvidate de)(?: (?:que|that|o que|lo de|de que))? (.{2,})$";
    // Verbos que são claramente "guarde isto".
    const string MemRememberRx = @"^(?:lembre-se|lembre se|lembra-se|lembrese|se lembre|memoriza|memorize|memorizar|aprende|aprenda|fica sabendo|saiba|keep in mind|ten en cuenta|aprende|memoriza)(?: (?:disso|isso|bem|esto|this))?(?: (?:que|de que|that))?:? (.+)$";
    // "lembra que …": memória se não tiver data/hora (com data, é lembrete).
    const string MemRememberThatRx = @"^(?:lembra|grava|guarda|anota|remember|note|recuerda|guarda) (?:que|that) (.+)$";

    static bool HasDate(string s)
    {
        var d = DateParser.Parse(s, Loc.Lang, DateTime.Now);
        return d.HasDay || d.HasTime;
    }

    // O trecho final da frase com as maiúsculas e a pontuação originais.
    static string RawTail(string raw, string lowerPiece)
    {
        lowerPiece = lowerPiece.Trim();
        int at = raw.ToLowerInvariant().LastIndexOf(lowerPiece, StringComparison.Ordinal);
        return at >= 0 ? raw.Substring(at).Trim() : lowerPiece;
    }

    string MemoryListReply()
    {
        var mem = _host.Settings.Memories;
        return mem.Count == 0 ? Loc.T("mem.empty") : Loc.F("mem.list", string.Join("\n", mem.Select(x => "• " + x)));
    }

    // ---------------- Ações compostas ----------------

    string AddTaskReply(string text, bool alarm)
    {
        var item = _host.AddTask(text.Trim(), alarm);
        if (item == null) return Loc.T("r.notUnderstood");
        var when = item.Due is DateTime d ? DateParser.Label(d, DateTime.Now) : "";
        if (item.Alarm)
            return item.Text == Loc.T("alarm.title") || item.Text.Length == 0
                ? Loc.F("r.alarmSet", when)
                : Loc.F("r.alarmSetText", when, item.Text);
        return item.Due != null ? Loc.F("r.reminderSet", when, item.Text) : Loc.F("r.noted", item.Text);
    }

    // "quais os meus lembretes?" / "o que eu tenho segunda?" / "tenho algo amanhã?" / "o que tenho essa semana?"
    string ListTasksReply(string question)
    {
        var now = DateTime.Now;
        var asked = DateParser.Parse(question, Loc.Lang, now);
        IEnumerable<TodoItem> tasks = _host.PendingTasks;
        string? scope = null;
        bool oneDay = false;

        if (asked.Week)
        {
            var end = now.Date.AddDays(7);
            tasks = tasks.Where(t => t.Due >= now.Date && t.Due < end);
            scope = Loc.T("date.thisWeek");
        }
        else if (asked.HasDay && asked.When is DateTime day)
        {
            tasks = tasks.Where(t => t.Due?.Date == day.Date);
            scope = DayName(day, now);
            oneDay = true;
        }

        var list = tasks.ToList();
        if (list.Count == 0) return scope != null ? Loc.F("r.nothingOn", scope) : Loc.T("r.noTasks");

        var items = string.Join("; ", list.Take(8).Select(t =>
            (t.Alarm ? "⏰ " : "") + t.Text +
            (t.Due is DateTime d ? " (" + (oneDay ? d.ToString("HH:mm") : DateParser.Label(d, now)) + ")" : "")));
        if (scope != null) return Loc.F("r.tasksOn", Capitalize(scope), items);
        var count = list.Count == 1 ? Loc.T("r.tasksCount1") : Loc.F("r.tasksCountN", list.Count);
        return Loc.F("r.tasksList", count, items);
    }

    static string DayName(DateTime day, DateTime now)
    {
        var weekday = day.ToString("dddd", Loc.Culture).Split('-')[0];
        if (day.Date == now.Date) return Loc.T("date.today");
        if (day.Date == now.Date.AddDays(1)) return $"{Loc.T("date.tomorrow")} ({weekday})";
        return $"{weekday}, {day:dd/MM}";
    }
    string RemoveTasksReply(string query, bool complete)
    {
        query = query.Trim();
        // "apaga todos os lembretes" → tudo
        if (Regex.IsMatch(Fold(query), @"^(todos|todas|tudo|all|everything|todo)$")) query = "";
        var removed = _host.RemoveTasks(query, complete);
        if (removed.Count == 0) return Loc.F("r.taskNotFound", query);
        var names = string.Join(", ", removed.Select(t => t.Text));
        return complete ? Loc.F("r.tasksDone", names) : Loc.F("r.tasksRemoved", names);
    }

    string AppVolume(string app, int? level, int? delta, bool? mute)
    {
        app = Regex.Replace(app.Trim(), @"\s+(?:no|na|em|on|in|en)\s+\d+.*$", "");
        // "o volume do meu computador" é o volume geral.
        if (Is(Fold(app), Grammar.Current.Master))
        {
            if (mute is bool mu)
            {
                _host.Audio.SetMute(mu);
                return mu ? Loc.T("r.muted") : Loc.T("r.unmuted");
            }
            int now = (int)Math.Round(Math.Max(0, _host.Audio.Volume) * 100);
            int target = Math.Clamp(level ?? now + (delta ?? 0), 0, 100);
            _host.Audio.SetVolume(target / 100f);
            return Loc.F("r.volume", target);
        }

        var changed = _host.Audio.SetAppVolume(app, level / 100f, delta / 100f, mute);
        var display = Capitalize(app);
        if (changed.Count == 0) return Loc.F("r.appNotPlaying", display);
        var s = changed[0];
        if (mute == true) return Loc.F("r.appMuted", s.Name);
        if (mute == false) return Loc.F("r.appUnmuted", s.Name);
        return Loc.F("r.appVolume", s.Name, Math.Round(s.Volume * 100));
    }

    async Task<string?> PlaySpotify(string query, string kind, CancellationToken ct)
    {
        var output = await _tools.ExecuteAsync("play_spotify", Args(("query", query), ("kind", kind)), ct);
        return output.Text;
    }

    async Task<string?> ChangeVolume(int delta, CancellationToken ct)
    {
        int now = (int)Math.Round(Math.Max(0, _host.Audio.Volume) * 100);
        int level = Math.Clamp(now + delta, 0, 100);
        await Tool("set_volume", ("level", level), ct);
        return Loc.F("r.volume", level);
    }

    string? BestProject(string name)
    {
        var found = _tools.SearchProjects(name);
        return found.Count > 0 && found[0].Score <= 1 ? found[0].Path : null;
    }

    async Task<string?> OpenProject(string name, string? program, bool canFallback, CancellationToken ct)
    {
        var path = BestProject(name);
        if (path == null) return canFallback ? null : Loc.F("r.projectNotFound", name);

        program ??= string.IsNullOrWhiteSpace(_host.Settings.PreferredEditor) ? null : _host.Settings.PreferredEditor;
        var args = program == null ? Args(("path", path)) : Args(("path", path), ("program", program));
        var output = await _tools.ExecuteAsync("open_path", args, ct);
        return output.IsError ? output.Text : Loc.F("r.opening", Path.GetFileName(path));
    }

    async Task<string?> BuildProject(string name, bool canFallback, CancellationToken ct)
    {
        var path = BestProject(name);
        var cmd = path == null ? null : AgentTools.BuildCommandFor(path);
        if (path == null || cmd == null)
            return canFallback ? null : path == null ? Loc.F("r.projectNotFound", name) : Loc.T("r.cantBuild");

        var output = await _tools.ExecuteAsync("run_command", Args(("command", cmd), ("working_directory", path)), ct);
        if (output.Text.StartsWith("O usuário negou")) return Loc.T("r.notBuilt");
        if (!output.IsError) return Loc.F("r.built", Path.GetFileName(path));

        var err = output.Text.Split('\n').Select(l => l.Trim())
            .LastOrDefault(l => Regex.IsMatch(l, @"\berr(or|o)\b", RegexOptions.IgnoreCase)) ?? "";
        if (err.Length > 160) err = err[..160] + "…";
        return Loc.F("r.buildFailed", Path.GetFileName(path), err).Trim();
    }

    async Task<string?> RunProject(string name, bool canFallback, CancellationToken ct)
    {
        var path = BestProject(name);
        var cmd = path == null ? null : AgentTools.RunCommandFor(path);
        if (path == null || cmd == null)
            return canFallback ? null : path == null ? Loc.F("r.projectNotFound", name) : Loc.T("r.cantRun");

        var output = await _tools.ExecuteAsync("run_command", Args(("command", cmd), ("working_directory", path), ("visible", true)), ct);
        return output.Text.StartsWith("O usuário negou") ? Loc.T("r.notRun") : Loc.F("r.running", Path.GetFileName(path));
    }

    async Task<string?> WindowAction(string target, string action, bool canFallback, CancellationToken ct)
    {
        var output = await _tools.ExecuteAsync("window_action", Args(("window", target), ("action", action)), ct);
        if (output.IsError) return canFallback ? null : output.Text;
        if (output.Text.StartsWith("O usuário")) return Loc.T("r.keptOpen");
        return action switch { "close" => Loc.T("r.closed"), "minimize" => Loc.T("r.minimized"), "maximize" => Loc.T("r.maximized"), _ => Loc.T("r.done") };
    }

    // ---------------- Utilitários ----------------

    async Task<string?> Tool(string name, (string Key, object? Value) arg, CancellationToken ct)
    {
        var output = await _tools.ExecuteAsync(name, Args(arg), ct);
        return output.IsError ? null : name == "media_control" ? Loc.T("r.ok") : output.Text;
    }

    static Dictionary<string, JsonElement> Args(params (string Key, object? Value)[] pairs) =>
        pairs.Where(p => p.Value != null).ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

    // Tira pontuação das pontas, o "Ei, Island" se ele vier junto na frase e o "por favor".
    static string Clean(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        t = Regex.Replace(t, @"^(ei|hey|hi|ok|okay|oi|oye|hola|ô|o|olá|ola)?[\s,]*(island|ailand|ilha|isla|ailende|jarvis)[\s,!.]*", "");
        t = Regex.Replace(t, @"^(por favor|pfv|pf|please)[\s,]+|[\s,]+(por favor|pfv|pf|please)$", "");
        return t.Trim(' ', '.', '!', '?', ',', ';', '¿', '¡');
    }

    // Remove acentos mantendo o mesmo tamanho do texto (para recortar trechos do original).
    internal static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c.ToString().Normalize(NormalizationForm.FormD)[0]);
        return sb.ToString();
    }

    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
    static bool Is(string n, string pattern) => Regex.IsMatch(n, pattern);
    static Match Rx(string n, string pattern) => Regex.Match(n, pattern);
}
