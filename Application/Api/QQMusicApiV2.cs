﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Application.Bean;
using Application.Exception;

namespace Application.Api
{
    public class QQMusicApiV2 : MusicApiV2Cacheable
    {
        private readonly QQMusicNativeApi _api;
        
        public QQMusicApiV2()
        {
            _api = new QQMusicNativeApi();
        }

        protected override IEnumerable<string> GetSongIdsFromAlbum0(string albumId)
        {
            // TODO 专辑功能待实现            
            return Array.Empty<string>();
        }

        protected override Dictionary<string, SongVo> GetSongVo0(string[] songIds, out Dictionary<string, string> errorMsgDict)
        {
            errorMsgDict = new Dictionary<string, string>();
            
            var result = new Dictionary<string, SongVo>();
            
            foreach (var songId in songIds)
            {
                var resp = _api.GetSong(songId);

                if (resp.Code != 0 || resp.Data.Length == 0)
                {
                    errorMsgDict[songId] = ErrorMsg.SONG_NOT_EXIST;
                    continue;
                }

                var song = resp.Data[0];

                result[songId] = new SongVo
                {
                    Links = null, // TODO 歌曲直链获取功能
                    Name = song.Name,
                    Singer = ContractSinger(song.Singer),
                    Album = song.Album.Name,
                    Duration = song.Interval * 1000
                };
                errorMsgDict[songId] = ErrorMsg.SUCCESS;
            }
            
            return result;
        }

        protected override LyricVo GetLyricVo0(string songId)
        {
            var resp = _api.GetLyric(songId);

            if (resp.Code == 0)
            {
                return new LyricVo
                {
                    Lyric = resp.lyric ?? string.Empty,
                    TranslateLyric = resp.trans ?? string.Empty
                };
            }

            throw new MusicLyricException(ErrorMsg.LRC_NOT_EXIST);
        }
        
        /// <summary>
        /// 拼接歌手名
        /// </summary>
        public static string ContractSinger(QQMusicBean.Singer[] singers)
        {
            if (singers == null || !singers.Any())
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var one in singers)
            {
                sb.Append(one.Name).Append(',');
            }

            return sb.Remove(sb.Length - 1, 1).ToString();
        }
    }
}