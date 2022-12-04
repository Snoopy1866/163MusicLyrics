﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using MusicLyricApp.Api;
using MusicLyricApp.Bean;
using MusicLyricApp.Exception;
using MusicLyricApp.Utils;
using Newtonsoft.Json;
using NLog;

namespace MusicLyricApp
{
    public partial class MainForm : MusicLyricForm
    {
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private readonly Dictionary<string, SaveVo> _globalSaveVoMap = new Dictionary<string, SaveVo>();

        private readonly SearchInfo _globalSearchInfo = new SearchInfo();

        private IMusicApiV2 _api;

        private SettingForm _settingForm;

        private UpgradeForm _upgradeForm;

        private ShortcutForm _shortcutForm;

        private BlurForm _blurForm;

        public MainForm()
        {
            // 禁止多开
            var instance = new Mutex(true, "MutexName", out var isNotRunning);
            if (!isNotRunning)
            {
                Environment.Exit(1);
            }
            
            InitializeComponent();
            AfterInitializeComponent();

            InitialConfig();

            TrySetHighDPIFont("Segoe UI");
        }

        private void InitialConfig()
        {
            // 1、加载配置
            SettingBean settingBean;
            if (File.Exists(Constants.SettingPath))
            {
                var text = File.ReadAllText(Constants.SettingPath);
                settingBean = text.ToEntity<SettingBean>();
            }
            else
            {
                settingBean = new SettingBean();
            }

            _globalSearchInfo.SettingBean = settingBean;
            _globalSearchInfo.SettingBeanBackup = settingBean.ToJson().ToEntity<SettingBean>();
            
            // 2、配置应用
            var paramConfig = settingBean.Config.RememberParam ? settingBean.Param : new PersistParamBean();
            
            OutputName_ComboBox.SelectedIndex = (int) paramConfig.OutputFileNameType;
            OutputEncoding_ComboBox.SelectedIndex = (int) paramConfig.Encoding;
            LrcType_ComboBox.SelectedIndex = (int) paramConfig.ShowLrcType;
            SearchSource_ComboBox.SelectedIndex = (int) paramConfig.SearchSource;
            SearchType_ComboBox.SelectedIndex = (int) paramConfig.SearchType;
            OutputFormat_CombBox.SelectedIndex = (int) paramConfig.OutputFileFormat;
            LrcMergeSeparator_TextBox.Text = paramConfig.LrcMergeSeparator;
            
            // 3、自动检查更新
            if (settingBean.Config.AutoCheckUpdate)
            {
                ThreadPool.QueueUserWorkItem(p => CheckLatestVersion(false));
            }
        }

        private void TrySetHighDPIFont(string fontName)
        {
            //缩放比例大于100%才更改字体
            if (DeviceDpi <= 96) return;

            Font font = null;
            try
            {
                font = new Font(fontName, 9F, FontStyle.Regular, GraphicsUnit.Point);
            }
            catch (System.Exception)
            {
                // ignored
            }

            if (font == null || !fontName.Equals(font.Name)) return;

            Type type = this.GetType();
            FieldInfo[] fieldInfos = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (FieldInfo fieldInfo in fieldInfos)
            {
                Type fieldType = fieldInfo.FieldType;
                if ("System.Windows.Forms".Equals(fieldType.Namespace))
                {
                    try
                    {
                        PropertyInfo propertyInfo = fieldType.GetProperty("Font");
                        Object obj = fieldInfo.GetValue(this);
                        propertyInfo.SetValue(obj, font);
                    }
                    catch (System.Exception)
                    {
                        // ignored
                    }
                }
            }
        }

        /// <summary>
        /// 读取搜索框并重新加载配置
        /// </summary>
        private void ReloadConfig()
        {
            ReloadInputIdText();

            _globalSearchInfo.SongIds.Clear();
            _globalSearchInfo.SettingBean.Param.LrcMergeSeparator = LrcMergeSeparator_TextBox.Text;

            ReloadMusicApi();
        }

        private void ReloadMusicApi()
        {
            if (_globalSearchInfo.SettingBean.Param.SearchSource == SearchSourceEnum.QQ_MUSIC)
            {
                _api = new QQMusicApiV2();
            }
            else
            {
                _api = new NetEaseMusicApiV2();
            }
        }

        private void ReloadInputIdText()
        {
            var inputText = Search_Text.Text.Trim();
            
            // 判断是否是目录
            if (Directory.Exists(inputText))
            {
                var subFileNameList = new List<string>();
                
                var directoryInfo = new DirectoryInfo(inputText);
                foreach (var info in directoryInfo.GetFileSystemInfos())
                {
                    if (info is DirectoryInfo)
                    {
                        // 文件夹，跳过处理，不做递归
                        continue;
                    }
                    else
                    {
                        var name = info.Name;

                        if (!string.IsNullOrWhiteSpace(info.Extension) && name.EndsWith(info.Extension))
                        {
                            name = name.Remove(name.Length - info.Extension.Length);
                        }

                        name = name.Trim();
                        
                        subFileNameList.Add(name);
                    }
                }

                _globalSearchInfo.InputIds = subFileNameList.ToArray();
            }
            else
            {
                // 不是目录，认为是实际的 ID
                var ids = Search_Text.Text.Trim().Split(',');
                _globalSearchInfo.InputIds = new string[ids.Length];
                for (var i = 0; i < ids.Length; i++)
                {
                    _globalSearchInfo.InputIds[i] = ids[i].Trim();
                }
            }
        }

        /// <summary>
        /// 根据歌曲ID查询
        /// </summary>
        /// <param name="songIds"></param>
        /// <param name="isVerbatimLyric"></param>
        /// <returns>songId, result</returns>
        private Dictionary<string, ResultVo<SaveVo>> SearchBySongId(string[] songIds, bool isVerbatimLyric)
        {
            var resultDict = new Dictionary<string, ResultVo<SaveVo>>();

            var songResp = _api.GetSongVo(songIds);
            foreach (var pair in songResp)
            {
                var songId = pair.Key;
                ResultVo<SaveVo> songResult;
                
                try
                {
                    var songVo = pair.Value.Assert().Data;
                    var lyricVo = _api.GetLyricVo(songVo.Id, songVo.DisplayId, isVerbatimLyric).Assert().Data;
                    
                    lyricVo.Duration = songVo.Duration;

                    songResult = new ResultVo<SaveVo>(new SaveVo(songId, songVo, lyricVo));
                }
                catch (WebException ex)
                {
                    _logger.Error(ex, "SearchBySongId network error, delay: {Delay}", NetworkUtils.GetWebRoundtripTime(50));
                    songResult = ResultVo<SaveVo>.Failure(ErrorMsg.NETWORK_ERROR);
                }
                catch (System.Exception ex)
                {
                    _logger.Error(ex, "SearchBySongId error, songId: {SongId}, message: {ErrorMsg}", songId, ex.Message);
                    songResult = ResultVo<SaveVo>.Failure(ex.Message);
                }

                resultDict.Add(songId, songResult);
            }
            
            return resultDict;
        }

        /// <summary>
        /// 初始化输入的歌曲 ID 列表
        /// </summary>
        private void InitInputSongIds()
        {
            var inputs = _globalSearchInfo.InputIds;
            if (inputs.Length < 1)
            {
                throw new MusicLyricException(ErrorMsg.INPUT_ID_ILLEGAL);
            }

            foreach (var input in inputs)
            {
                var searchSource = _globalSearchInfo.SettingBean.Param.SearchSource;
                var searchType = _globalSearchInfo.SettingBean.Param.SearchType;
                
                var id = GlobalUtils.CheckInputId(input, searchSource, searchType);
                switch (searchType)
                {
                    case SearchTypeEnum.ALBUM_ID:
                        foreach (var simpleSongVo in _api.GetAlbumVo(id).Assert().Data.SimpleSongVos)
                        {
                            _globalSearchInfo.SongIds.Add(simpleSongVo.DisplayId);
                        }
                        break;
                    case SearchTypeEnum.PLAYLIST_ID:
                        foreach (var simpleSongVo in _api.GetPlaylistVo(id).Assert().Data.SimpleSongVos)
                        {
                            _globalSearchInfo.SongIds.Add(simpleSongVo.DisplayId);
                        }
                        break;
                    case SearchTypeEnum.SONG_ID:
                        _globalSearchInfo.SongIds.Add(id);
                        break;
                    default:
                        throw new MusicLyricException(ErrorMsg.SYSTEM_ERROR);
                }
            }
        }

        /// <summary>
        /// 单个歌曲搜索
        /// </summary>
        /// <param name="songId">歌曲ID</param>
        private void SingleSearch(string songId)
        {
            var isVerbatimLyric = _globalSearchInfo.SettingBean.Config.EnableVerbatimLyric;

            var resDict = SearchBySongId(new[] { songId }, isVerbatimLyric);
            var result = resDict[songId].Assert().Data;
            
            // 加入结果集
            _globalSaveVoMap.Add(songId, result);

            // 前端设置
            SongName_TextBox.Text = result.SongVo.Name;
            Singer_TextBox.Text = result.SongVo.Singer;
            Album_TextBox.Text = result.SongVo.Album;
            UpdateLrcTextBox(string.Empty);
        }

        /// <summary>
        /// 批量歌曲搜索
        /// </summary>
        private void BatchSearch(IEnumerable<string> ids)
        {
            var isVerbatimLyric = _globalSearchInfo.SettingBean.Config.EnableVerbatimLyric;
            var resultMaps = SearchBySongId(ids.ToArray(), isVerbatimLyric);

            // 输出日志
            var log = new StringBuilder();

            foreach (var kvp in resultMaps)
            {
                var songId = kvp.Key;
                var resultVo = kvp.Value;

                log.Append($"{songId}");
                
                if (resultVo.IsSuccess())
                {
                    var saveVo = resultVo.Data;
                    _globalSaveVoMap.Add(songId, saveVo);
                    
                    log.Append($" => {saveVo.SongVo.Name}");
                }

                log
                    .Append($" => {resultVo.ErrorMsg}")
                    .Append(Environment.NewLine);
            }

            log
                .Append(Environment.NewLine)
                .Append($"Total {resultMaps.Count} Success {_globalSaveVoMap.Count} Failure {resultMaps.Count - _globalSaveVoMap.Count}")
                .Append(Environment.NewLine);

            UpdateLrcTextBox(log.ToString());
        }

        /// <summary>
        /// 搜索按钮，点击事件
        /// </summary>
        public async void Search_Btn_Click(object sender, EventArgs e)
        {
            ReloadConfig();
            CleanTextBox();
            _globalSaveVoMap.Clear();

            try
            {
                InitInputSongIds();

                var songIds = _globalSearchInfo.SongIds;
                if (songIds.Count > 1)
                {
                    BatchSearch(songIds);
                }
                else
                { 
                    SingleSearch(songIds.First());
                }
            }
            catch (WebException ex)
            {
                _logger.Error(ex, "Search Network error, delay: {Delay}", await NetworkUtils.GetWebRoundtripTimeAsync());
                MessageBox.Show(ErrorMsg.NETWORK_ERROR, "错误");
            }
            catch (MusicLyricException ex)
            {
                _logger.Error("Search Business failed, param: {SearchParam}, type: {SearchType}, message: {ErrorMsg}",
                    Search_Text.Text, _globalSearchInfo.SettingBean.Param.SearchType, ex.Message);
                MessageBox.Show(ex.Message, "提示");
            }
            catch (System.Exception ex)
            {
                _logger.Error(ex);
                MessageBox.Show(ErrorMsg.SYSTEM_ERROR, "错误");
            }
        }

        private async void Blur_Search_Btn_Click(object sender, EventArgs e)
        {
            try
            {
                var keyword = Search_Text.Text.Trim();
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    throw new MusicLyricException(ErrorMsg.INPUT_CONENT_EMPLT);
                }
                
                ReloadMusicApi();

                var resultVo = _api.Search(keyword, _globalSearchInfo.SettingBean.Param.SearchType).Assert().Data;
                if (resultVo.IsEmpty())
                {
                    throw new MusicLyricException(ErrorMsg.SEARCH_RESULT_EMPTY);
                }
                
                FormUtils.OpenForm(_blurForm, () => _blurForm = new BlurForm(resultVo, this), this);
            }
            catch (WebException ex)
            {
                _logger.Error(ex, "Blur Search Network error, delay: {Delay}", await NetworkUtils.GetWebRoundtripTimeAsync());
                MessageBox.Show(ErrorMsg.NETWORK_ERROR, "错误");
            }
            catch (MusicLyricException ex)
            {
                _logger.Error("Blur Search Business failed, param: {SearchParam}, type: {SearchType}, message: {ErrorMsg}",
                    Search_Text.Text, _globalSearchInfo.SettingBean.Param.SearchType, ex.Message);
                MessageBox.Show(ex.Message, "提示");
            }
            catch (System.Exception ex)
            {
                _logger.Error(ex);
                MessageBox.Show(ErrorMsg.SYSTEM_ERROR, "错误");
            }
        }
        
        /// <summary>
        /// 获取歌曲链接按钮，点击事件
        /// </summary>
        private void SongLink_Btn_Click(object sender, EventArgs e)
        {
            if (_globalSaveVoMap == null || _globalSaveVoMap.Count == 0)
            {
                MessageBox.Show(ErrorMsg.MUST_SEARCH_BEFORE_GET_SONG_URL, "提示");
                return;
            }

            if (_globalSaveVoMap.Count > 1)
            {
                var csv = new CsvBean();
                
                csv.AddColumn("id");
                csv.AddColumn("songLink");
    
                foreach (var songId in _globalSearchInfo.SongIds)
                {
                    _globalSaveVoMap.TryGetValue(songId, out var saveVo);

                    csv.AddData(songId);
                    csv.AddData(_api.GetSongLink(songId).Data);
                    csv.NextLine();
                }

                UpdateLrcTextBox(csv.ToString());
            }
            else
            {
                var link = _api.GetSongLink(_globalSaveVoMap.Keys.First());
                if (link.IsSuccess())
                {
                    Clipboard.SetDataObject(link.Data);
                    MessageBox.Show(ErrorMsg.SONG_URL_GET_SUCCESS, "提示");
                }
                else
                {
                    MessageBox.Show(ErrorMsg.SONG_URL_GET_FAILED, "提示");
                }
            }
        }

        /// <summary>
        /// 获取歌曲封面按钮，点击事件
        /// </summary>
        private void SongPic_Btn_Click(object sender, EventArgs e)
        {
            if (_globalSaveVoMap == null || _globalSaveVoMap.Count == 0)
            {
                MessageBox.Show(ErrorMsg.MUST_SEARCH_BEFORE_GET_SONG_PIC, "提示");
                return;
            }

            if (_globalSaveVoMap.Count > 1)
            {
                var csv = new CsvBean();
                
                csv.AddColumn("id");
                csv.AddColumn("picLink");
                
                foreach (var songId in _globalSearchInfo.SongIds)
                {
                    _globalSaveVoMap.TryGetValue(songId, out var saveVo);

                    csv.AddData(songId);
                    csv.AddData(saveVo == null ? string.Empty : saveVo.SongVo.Pics);
                    csv.NextLine();
                }

                UpdateLrcTextBox(csv.ToString());
            }
            else
            {
                // only loop one times
                foreach (var item in _globalSaveVoMap)
                {
                    var pic = item.Value.SongVo.Pics;
                    if (pic == null)
                    {
                        MessageBox.Show(ErrorMsg.SONG_PIC_GET_FAILED, "提示");
                    }
                    else
                    {
                        Clipboard.SetDataObject(pic);
                        MessageBox.Show(ErrorMsg.SONG_PIC_GET_SUCCESS, "提示");
                    }
                }
            }
        }
        
        /**
         * 单个保存
         */
        private async void SingleSave(string songId)
        {
            // 没有搜索结果
            if (!_globalSaveVoMap.TryGetValue(songId, out var saveVo))
            {
                MessageBox.Show(ErrorMsg.MUST_SEARCH_BEFORE_SAVE, "提示");
                return;
            }

            // 没有歌词内容
            if (saveVo.LyricVo.IsEmpty())
            {
                MessageBox.Show(ErrorMsg.LRC_NOT_EXIST, "提示");
                return;
            }
            
            var saveDialog = new SaveFileDialog();
            saveDialog.FileName = GlobalUtils.GetOutputName(saveVo.SongVo, _globalSearchInfo.SettingBean.Param.OutputFileNameType);
            saveDialog.Filter = _globalSearchInfo.SettingBean.Param.OutputFileFormat.ToDescription();

            if (saveDialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }
            
            try
            {
                using (var sw = new StreamWriter(saveDialog.FileName, false, GlobalUtils.GetEncoding(_globalSearchInfo.SettingBean.Param.Encoding))) 
                {
                    await sw.WriteAsync(await LyricUtils.GetOutputContent(saveVo.LyricVo, _globalSearchInfo));
                    await sw.FlushAsync();
                }

                MessageBox.Show(string.Format(ErrorMsg.SAVE_COMPLETE, 1, 0), "提示");
            }
            catch (System.Exception ew)
            {
                _logger.Error(ew, "单独保存歌词失败");
                MessageBox.Show("保存失败！错误信息：\n" + ew.Message);
            }
        }

        /**
         * 批量保存
         */
        private async void BatchSave()
        {
            var saveDialog = new SaveFileDialog();
            saveDialog.FileName = "直接选择保存路径即可，无需修改此处内容";
            saveDialog.Filter = _globalSearchInfo.SettingBean.Param.OutputFileFormat.ToDescription();

            if (saveDialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }
            
            // 保存
            var skipCount = 0;
            var success = new HashSet<string>();
            
            try
            {
                var localFilePath = saveDialog.FileName;
                // 获取文件后缀
                var fileSuffix = localFilePath.Substring(localFilePath.LastIndexOf("."));
                //获取文件路径，不带文件名 
                var filePath = localFilePath.Substring(0, localFilePath.LastIndexOf("\\"));
                    
                foreach (var item in _globalSaveVoMap)
                {
                    var saveVo = item.Value;
                    var lyricVo = saveVo.LyricVo;
                    if (lyricVo.IsEmpty())
                    {
                        skipCount++;
                        continue;
                    }

                    var path = filePath + '/' + GlobalUtils.GetOutputName(saveVo.SongVo, _globalSearchInfo.SettingBean.Param.OutputFileNameType) + fileSuffix;
                    using(var sw = new StreamWriter(path, false, GlobalUtils.GetEncoding(_globalSearchInfo.SettingBean.Param.Encoding)))
                    {
                        await sw.WriteAsync(await LyricUtils.GetOutputContent(lyricVo, _globalSearchInfo));
                        await sw.FlushAsync();
                        success.Add(item.Key);
                    }
                }
                
                MessageBox.Show(string.Format(ErrorMsg.SAVE_COMPLETE, success.Count, skipCount), "提示");
            }
            catch (System.Exception ew)
            {
                _logger.Error(ew, "批量保存失败");
                MessageBox.Show("批量保存失败，错误信息：\n" + ew.Message);
            }

            // 输出日志
            var log = new StringBuilder();
            foreach (var songId in _globalSearchInfo.SongIds)
            {
                log
                    .Append($"{songId} => {(success.Contains(songId) ? "success" : "failure")}")
                    .Append(Environment.NewLine);
            }
            UpdateLrcTextBox(log.ToString());
        }

        /**
         * 保存按钮点击事件
         */
        private void Save_Btn_Click(object sender, EventArgs e)
        {
            if (_globalSaveVoMap == null || _globalSaveVoMap.Count == 0)
            {
                MessageBox.Show(ErrorMsg.MUST_SEARCH_BEFORE_SAVE, "提示");
                return;
            }

            if (_globalSaveVoMap.Count > 1)
            {
                BatchSave();
            }
            else
            {
                // only loop one times
                foreach (var item in _globalSaveVoMap)
                {
                    SingleSave(item.Value.SongId);
                }
            }
        }

        /**
         * 更新前端歌词
         */
        private async void UpdateLrcTextBox(string replace)
        {
            if (replace != string.Empty)
            {
                Console_TextBox.Text = replace;
            }
            else
            {
                // 根据最新配置，更新输出歌词
                if (_globalSaveVoMap != null && _globalSaveVoMap.Count == 1)
                {
                    var lyricVo = _globalSaveVoMap.Values.First().LyricVo;
                    if (lyricVo.IsEmpty())
                    {
                        Console_TextBox.Text = ErrorMsg.LRC_NOT_EXIST;
                    }
                    else
                    {
                        Console_TextBox.Text = await LyricUtils.GetOutputContent(lyricVo, _globalSearchInfo);
                    }
                }
            }
        }

        /**
         * 清空前端内容
         */
        private void CleanTextBox()
        {
            Console_TextBox.Text = string.Empty;
            SongName_TextBox.Text = string.Empty;
            Singer_TextBox.Text = string.Empty;
            Album_TextBox.Text = string.Empty;
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 如果参数不记住，需要回滚
            if (!_globalSearchInfo.SettingBean.Config.RememberParam)
            {
                _globalSearchInfo.SettingBean.Param = _globalSearchInfo.SettingBeanBackup.Param;
            }
            
            // 配置持久化
            File.WriteAllText(Constants.SettingPath, _globalSearchInfo.SettingBean.ToJson(), Encoding.UTF8);
        }

        private void MainForm_MouseEnter(object sender, EventArgs e)
        {
            if (_globalSearchInfo.SettingBean.Config.AutoReadClipboard)
            {
                Search_Text.Text = Clipboard.GetText();
            }
        }

        /// <summary>
        /// 搜索来源下拉框，属性变更事件
        /// </summary>
        private void SearchSource_ComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            _globalSearchInfo.SettingBean.Param.SearchSource = (SearchSourceEnum)SearchSource_ComboBox.SelectedIndex;

            ReloadConfig();
            UpdateLrcTextBox(string.Empty);
        }

        /// <summary>
        /// 搜索类型下拉框，属性变更事件
        /// </summary>
        private void SearchType_ComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            _globalSearchInfo.SettingBean.Param.SearchType = (SearchTypeEnum)SearchType_ComboBox.SelectedIndex;

            ReloadConfig();
            UpdateLrcTextBox(string.Empty);
        }

        /// <summary>
        /// LRC歌词类型，属性变更事件
        /// </summary>
        private void LrcType_ComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            switch (_globalSearchInfo.SettingBean.Param.ShowLrcType = (ShowLrcTypeEnum)LrcType_ComboBox.SelectedIndex)
            {
                case ShowLrcTypeEnum.ORIGIN_PRIOR_MERGE:
                case ShowLrcTypeEnum.TRANSLATE_PRIOR_MERGE: 
                    LrcMergeSeparator_TextBox.ReadOnly = false;
                    LrcMergeSeparator_TextBox.BackColor = Color.White;
                    break;
                default: 
                    LrcMergeSeparator_TextBox.Text = null;
                    LrcMergeSeparator_TextBox.ReadOnly = true;
                    LrcMergeSeparator_TextBox.BackColor = Color.FromArgb(240, 240, 240);
                    break;
            }

            ReloadConfig();
            UpdateLrcTextBox(string.Empty);
        }

        /// <summary>
        /// LRC歌词合并符，内容变更事件
        /// </summary>
        private void LrcMergeSeparator_TextBox_TextChanged(object sender, EventArgs e)
        {
            ReloadConfig();
            UpdateLrcTextBox(string.Empty);
        }

        /// <summary>
        /// 窗体键盘事件
        /// </summary>
        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S)
            {
                // 保存操作
                Save_Btn_Click(sender, e);
            } else if (e.Control && e.KeyCode == Keys.Enter)
            {
                // 模糊搜索
                Blur_Search_Btn_Click(sender, e);
            } else if (e.KeyCode == Keys.Enter)
            {
                // 精确搜索
                Search_Btn_Click(sender, e);
            }
        }

        /// <summary>
        /// 配置下拉框，属性变更事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Config_ComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!(sender is ComboBox input))
            {
                throw new MusicLyricException(ErrorMsg.SYSTEM_ERROR);
            }
            
            if (input == OutputEncoding_ComboBox)
            {
                _globalSearchInfo.SettingBean.Param.Encoding = (OutputEncodingEnum)input.SelectedIndex;
            } 
            else if (input == OutputFormat_CombBox)
            {
                _globalSearchInfo.SettingBean.Param.OutputFileFormat = (OutputFormatEnum)input.SelectedIndex;
            }
            else if (input == OutputName_ComboBox)
            {
                _globalSearchInfo.SettingBean.Param.OutputFileNameType = (OutputFilenameTypeEnum)input.SelectedIndex;
            }
            
            ReloadConfig();
        }

        /// <summary>
        /// 上方菜单元素，点击事件
        /// </summary>
        private async void Top_MItem_Click(object sender, EventArgs e)
        {
            if (!(sender is ToolStripMenuItem input))
            {
                throw new MusicLyricException(ErrorMsg.SYSTEM_ERROR);
            }
            
            if (input == Home_MItem)
            {
                try
                {
                    Process.Start("https://github.com/jitwxs/163MusicLyrics");
                }
                catch (System.Exception ex)
                {
                    _logger.Error(ex, "项目主页打开失败,网络延迟: {0}", await NetworkUtils.GetWebRoundtripTimeAsync());
                    MessageBox.Show("项目主页打开失败", "错误");
                }
            } 
            else if (input == Wiki_MItem)
            {
                try
                {
                    Process.Start("https://github.com/jitwxs/163MusicLyrics/wiki");
                }
                catch (System.Exception ex)
                {
                    _logger.Error(ex, "使用手册网址打开失败");
                    MessageBox.Show("使用手册网址打开失败", "错误");
                }
            }
            else if (input == Setting_MItem)
            {
                FormUtils.OpenForm(_settingForm, () => _settingForm = new SettingForm(_globalSearchInfo.SettingBean), this);
            }
            else if (input == Issue_MItem)
            {
                try
                {
                    Process.Start("https://github.com/jitwxs/163MusicLyrics/issues");
                }
                catch (System.Exception ex)
                {
                    _logger.Error(ex, "问题反馈网址打开失败");
                    MessageBox.Show("问题反馈网址打开失败", "错误");
                }
            }
            else if (input == CheckVersion_MItem)
            {
                ThreadPool.QueueUserWorkItem(p => CheckLatestVersion(true));
            }
            else if (input == ShortCut_MItem)
            {
                FormUtils.OpenForm(_shortcutForm, () => _shortcutForm = new ShortcutForm(), this);
            }
        }

        private bool _inCheckVersion;

        private bool _showMessageIfNotExistLatestVersion;

        private void CheckLatestVersion(bool showMessageIfNotExistLatestVersion)
        {
            _showMessageIfNotExistLatestVersion = showMessageIfNotExistLatestVersion;
            
            if (_inCheckVersion)
            {
                return;
            }

            _inCheckVersion = true;

            try
            {
                // support https
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                var jsonStr = HttpUtils.HttpGetAsync(
                    "https://api.github.com/repos/jitwxs/163MusicLyrics/releases/latest", 
                    "application/json", 
                    new Dictionary<string, string>
                    {
                        { "Accept", "application/vnd.github.v3+json" },
                        { "User-Agent", BaseNativeApi.Useragent }
                    }).Result;

                var info = JsonConvert.DeserializeObject<GitHubInfo>(jsonStr);
                if (info == null)
                {
                    MessageBox.Show(ErrorMsg.GET_LATEST_VERSION_FAILED, "提示");
                    return;
                }

                if (info.Message != null && info.Message.Contains("API rate limit"))
                {
                    MessageBox.Show(ErrorMsg.API_RATE_LIMIT, "提示");
                    return;
                }

                string bigV = info.TagName.Substring(1, 2), smallV = info.TagName.Substring(3);
                string curBigV = Constants.Version.Substring(1, 2), curSmallV = Constants.Version.Substring(3);

                if (bigV.CompareTo(curBigV) == 1 || (bigV.CompareTo(curBigV) == 0 && smallV.CompareTo(curSmallV) == 1))
                {
                    void Action()
                    {
                        FormUtils.OpenForm(_upgradeForm, () => _upgradeForm = new UpgradeForm(info), this);
                    }
                    Invoke((Action)Action);
                }
                else if (_showMessageIfNotExistLatestVersion)
                {
                    MessageBox.Show(ErrorMsg.THIS_IS_LATEST_VERSION, "提示");
                }
            }
            catch (System.Exception ex)
            {
                _logger.Error(ex);
                MessageBox.Show(ErrorMsg.NETWORK_ERROR, "提示");
            }
            finally
            {
                _inCheckVersion = false;
            }
        }
    }
}