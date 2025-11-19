using MailDaemon.Settings;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data;
using System.Net.Mail;
using System.Net.Mime;

namespace MailDaemon.Services
{
    public class DaemonService
    {
        private readonly ILogger<DaemonService> _logger;
        private readonly MailSettings _mail;
        private readonly DaemonSettings _settings;

        public DaemonService(
            ILogger<DaemonService> logger,
            IOptions<MailSettings> mail,
            IOptions<DaemonSettings> settings)
        {
            _logger = logger;
            _mail = mail.Value;
            _settings = settings.Value;
        }

        private string Table(string name) =>
            $"{_settings.Schema}{name}";

        // ======================================================
        // ===============   MÉTODO PRINCIPAL   =================
        // ======================================================
        public async Task ProcesarTareas()
        {
            using var conn = new SqlConnection(_settings.SqlConnection);
            await conn.OpenAsync();

            string sql = $@"
                SELECT  
                    Id,
                    IdTemplate,
                    Nombre,
                    StoredProcedure,
                    TipoProgramacion,
                    Fecha,
                    Hora,
                    DiaSemana,
                    CadaMinutos
                FROM {Table("MailTasks")}
                WHERE Activo = 1
                  AND (ProximoEnvio IS NULL OR ProximoEnvio <= GETDATE())
            ";

            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();

            var tareas = new List<(int Id, int TemplateId, string Nombre, string StoredProcedure,
                                   string Tipo, DateTime? Fecha, TimeSpan? Hora,
                                   int? DiaSemana, int? CadaMinutos)>();

            while (await reader.ReadAsync())
            {
                tareas.Add((
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    reader.IsDBNull(6) ? null : reader.GetTimeSpan(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8)
                ));
            }

            reader.Close();

            foreach (var tarea in tareas)
            {
                try
                {
                    _logger.LogInformation("Procesando tarea {id}", tarea.Id);

                    await ProcesarTarea(
                        conn,
                        tarea.Id,
                        tarea.TemplateId,
                        tarea.StoredProcedure,
                        tarea.Tipo,
                        tarea.Fecha,
                        tarea.Hora,
                        tarea.DiaSemana,
                        tarea.CadaMinutos
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error procesando tarea {id}", tarea.Id);
                }
            }
        }

        // ======================================================
        // ===============   PROCESAR UNA TAREA   ===============
        // ======================================================
        private async Task ProcesarTarea(
            SqlConnection conn,
            int taskId,
            int templateId,
            string storedProcedure,
            string tipoProg,
            DateTime? fecha,
            TimeSpan? hora,
            int? diaSemana,
            int? cadaMinutos)
        {
            // DESTINATARIOS
            var destinatarios = await ObtenerDestinatarios(conn, taskId);

            // IMÁGENES EMBEBIDAS
            var imagenes = await ObtenerImagenes(conn, templateId);

            // VALORES DINÁMICOS (Contrato, Gerencia, etc.)
            var dominios = await ObtenerValoresDeTarea(conn, taskId);

            foreach (var (dominio, valor) in dominios)
            {
                try
                {
                    string html = await EjecutarStoredProcedure(
                            conn,
                            storedProcedure,
                            dominio,
                            valor,
                            DateTime.Today
                        );

                    // Cargar CSS desde el archivo
                    string estilos = await ObtenerArchivoEstilo(conn, templateId);

                    // Insertar CSS en marcador @Estilos@
                    string htmlFinal = html.Replace("@Estilos@", $"<style>{estilos}</style>");

                    EnviarCorreo(htmlFinal, destinatarios, imagenes);

                    _logger.LogInformation("Correo enviado para dominio {dominio} = {valor}", dominio, valor);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error enviando correo dominio {d}/{v}", dominio, valor);
                }
            }

            // SIGUIENTE ENVÍO
            DateTime proximo = CalcularProximoEnvio(tipoProg, fecha, hora, diaSemana, cadaMinutos);

            // REGISTRAR
            await RegistrarEnvio(conn, taskId, true, "OK", proximo);
        }

        // ======================================================
        // ===============   EJECUTAR STORED PROC   =============
        // ======================================================
        private async Task<string> EjecutarStoredProcedure(
    SqlConnection conn,
    string storedProcedure,
    string dominio,
    string valor,
    DateTime fecha)
        {
            _logger.LogInformation("→ Ejecutando SP '{sp}' para Dominio={dom} Valor={val}",
                storedProcedure, dominio, valor);

            using var cmd = new SqlCommand(storedProcedure, conn);
            cmd.CommandType = CommandType.StoredProcedure;

            // === Parámetros dinámicos según dominio ===
            switch (dominio.ToUpper())
            {
                case "CONTRATO":
                    cmd.Parameters.AddWithValue("@Contrato", valor);
                    break;

                case "GERENCIA":
                    cmd.Parameters.AddWithValue("@Gerencia", valor);
                    break;

                case "FAENA":
                    cmd.Parameters.AddWithValue("@Faena", valor);
                    break;

                case "TURNO":
                    cmd.Parameters.AddWithValue("@Turno", valor);
                    break;

                default:
                    throw new Exception($"Dominio no soportado: {dominio}");
            }

            cmd.Parameters.AddWithValue("@Fecha", fecha);

            // === Param OUTPUT ===
            var pHtml = new SqlParameter("@HtmlBody", SqlDbType.NVarChar, -1)
            {
                Direction = ParameterDirection.Output
            };

            cmd.Parameters.Add(pHtml);

            // Ejecutar
            await cmd.ExecuteNonQueryAsync();

            string resultado = pHtml.Value?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(resultado))
            {
                _logger.LogError("❌ SP ejecutado pero el parámetro @HtmlBody llegó VACÍO.");
            }
            else
            {
                _logger.LogWarning("✔ SP devolvió HTML con longitud: {len}", resultado.Length);
            }

            return resultado;
        }


        // ======================================================
        // ===============   DESTINATARIOS   ====================
        // ======================================================
        private async Task<List<(string Email, string Tipo)>> ObtenerDestinatarios(SqlConnection conn, int taskId)
        {
            string sql = $@"
                SELECT R.Email, TE.Nombre
                FROM {Table("MailTaskRecipients")} M
                INNER JOIN {Table("MailRecipients")} R ON R.Id = M.RecipientId
                INNER JOIN {Table("TipoEnvio")} TE ON TE.Id = M.IdTipoEnvio
                WHERE M.MailTaskId = @id
            ";

            var list = new List<(string, string)>();

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", taskId);

            using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                list.Add((rd.GetString(0), rd.GetString(1)));
            }
            return list;
        }

        // ======================================================
        // ===============   IMÁGENES CID   =====================
        // ======================================================
        private async Task<List<(string ContentId, string Ruta, string TipoImagen)>> ObtenerImagenes(SqlConnection conn, int templateId)
        {
            string sql = $@"
                SELECT ContentId, RutaArchivo, ISNULL(TipoImagen, 'NORMAL')
                FROM {Table("MailEmbeddedImages")}
                WHERE IdTemplate = @id
            ";

            var list = new List<(string, string, string)>();

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", templateId);

            using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                list.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2)));
            }

            return list;
        }

        // ======================================================
        // ===============   ENVIAR CORREO   ====================
        // ======================================================
        private void EnviarCorreo(
            string html,
            List<(string Email, string Tipo)> destinatarios,
            List<(string ContentId, string Ruta, string TipoImagen)> imagenes)
        {
            using var msg = new MailMessage();
            msg.From = new MailAddress(_mail.FromEmail, _mail.FromName);
            msg.Subject = "Reporte Automático";
            msg.IsBodyHtml = true;

            foreach (var d in destinatarios)
            {
                if (d.Tipo == "TO") msg.To.Add(d.Email);
                else if (d.Tipo == "CC") msg.CC.Add(d.Email);
                else msg.Bcc.Add(d.Email);
            }

            AlternateView htmlView = AlternateView.CreateAlternateViewFromString(
                html,
                null,
                MediaTypeNames.Text.Html
            );

            foreach (var img in imagenes)
            {
                if (!File.Exists(img.Ruta))
                {
                    _logger.LogWarning("Imagen no encontrada: {ruta}", img.Ruta);
                    continue;
                }

                string mime =
                    img.Ruta.EndsWith(".png") ? "image/png" :
                    (img.Ruta.EndsWith(".jpg") || img.Ruta.EndsWith(".jpeg")) ? "image/jpeg" :
                    "application/octet-stream";

                var lr = new LinkedResource(img.Ruta, mime)
                {
                    ContentId = img.ContentId
                };

                htmlView.LinkedResources.Add(lr);
            }

            msg.AlternateViews.Add(htmlView);

            using var smtp = new SmtpClient(_mail.Host, _mail.Port)
            {
                EnableSsl = _mail.EnableSsl
            };

            smtp.Send(msg);
        }

        // ======================================================
        // ===============   VALORES DE TAREA   =================
        // ======================================================
        private async Task<List<(string Dominio, string Valor)>> ObtenerValoresDeTarea(SqlConnection conn, int taskId)
        {
            string sql = $@"
                SELECT Dominio, Valor
                FROM {Table("MailTaskDomainValues")}
                WHERE MailTaskId = @id AND Activo = 1
            ";

            var lista = new List<(string, string)>();

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", taskId);

            using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                lista.Add((rd.GetString(0), rd.GetString(1)));
            }

            return lista;
        }

        // ======================================================
        // ===============   PROXIMO ENVÍO   ====================
        // ======================================================
        private DateTime CalcularProximoEnvio(
            string tipo,
            DateTime? fecha,
            TimeSpan? hora,
            int? diaSemana,
            int? cadaMinutos)
        {
            var ahora = DateTime.Now;

            return tipo switch
            {
                "UNICO" => DateTime.MaxValue,

                "CADA_X_MINUTOS" => ahora.AddMinutes(cadaMinutos ?? 1),

                "DIARIO" =>
                    ahora.Date.Add(hora ?? TimeSpan.FromHours(8)) <= ahora
                        ? ahora.Date.AddDays(1).Add(hora ?? TimeSpan.FromHours(8))
                        : ahora.Date.Add(hora ?? TimeSpan.FromHours(8)),

                "SEMANAL" => ObtenerFechaSemanal(ahora, diaSemana ?? 1, hora),

                "MENSUAL" => ObtenerFechaMensual(ahora, fecha, hora),

                _ => ahora.AddMinutes(5)
            };
        }

        private DateTime ObtenerFechaSemanal(DateTime ahora, int diaSemana, TimeSpan? hora)
        {
            int hoy = (int)ahora.DayOfWeek;
            int delta = ((diaSemana - hoy + 7) % 7);
            var fechaBase = ahora.Date.AddDays(delta).Add(hora ?? TimeSpan.FromHours(8));

            return fechaBase <= ahora ? fechaBase.AddDays(7) : fechaBase;
        }

        private DateTime ObtenerFechaMensual(DateTime ahora, DateTime? fecha, TimeSpan? hora)
        {
            if (fecha == null) fecha = DateTime.Today;

            int day = fecha.Value.Day;
            int max = DateTime.DaysInMonth(ahora.Year, ahora.Month);
            day = Math.Min(day, max);

            var baseFecha = new DateTime(ahora.Year, ahora.Month, day)
                .Add(hora ?? TimeSpan.FromHours(8));

            return baseFecha <= ahora ? baseFecha.AddMonths(1) : baseFecha;
        }

        // ======================================================
        // ===================   LOG   ==========================
        // ======================================================
        private async Task RegistrarEnvio(SqlConnection conn, int taskId, bool exito, string msg, DateTime proximo)
        {
            string sql = $@"
                INSERT INTO {Table("MailTaskLog")} (MailTaskId, Exitoso, Mensaje, FechaEnvio)
                VALUES (@id, @ex, @msg, GETDATE());

                UPDATE {Table("MailTasks")}
                SET UltimoEnvio = GETDATE(),
                    ProximoEnvio = @prox
                WHERE Id = @id;
            ";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", taskId);
            cmd.Parameters.AddWithValue("@ex", exito);
            cmd.Parameters.AddWithValue("@msg", msg);
            cmd.Parameters.AddWithValue("@prox", proximo);

            await cmd.ExecuteNonQueryAsync();
        }
        // =============================================
        // ==============EStilos
        private async Task<string> ObtenerArchivoEstilo(SqlConnection conn, int templateId)
        {
            string sql = $@"
                SELECT EstiloArchivo
                FROM {Table("MailTemplates")}
                WHERE Id = @id
            ";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", templateId);

            string? fileName = (await cmd.ExecuteScalarAsync())?.ToString();

            if (string.IsNullOrWhiteSpace(fileName))
                return "";

            string fullPath = Path.Combine(AppContext.BaseDirectory, "Styles", fileName);

            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("⚠ Archivo de estilos no encontrado: {archivo}", fullPath);
                return "";
            }

            return await File.ReadAllTextAsync(fullPath);
         }

    }
}
