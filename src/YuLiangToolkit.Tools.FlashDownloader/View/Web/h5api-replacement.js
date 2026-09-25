// =============================================================================
// 4399 h5 小游戏的「替代 API」。
//
// 背景：4399 的 h5 游戏都会去加载 https://h.api.4399.com/h5mini-2.0/h5api-interface.php
// 拿广告 / 排行榜 / 登录 / 存档等能力。游戏下到本地之后，这个官方接口要么跨域失败、
// 要么因为没登录而弹一堆东西，广告也点不动，于是游戏卡住。
//
// 做法：下载 h5 游戏时，把页面里的官方接口地址整体替换成 /tool/substiH5WanApi.js
// （就是本文件，由「Flash 下载器」的本地服务器提供），于是游戏以为自己在跟官方说话。
//
// 这里只把「看广告」实现成 0 秒白送收益，其余接口一律空实现 ——
// 所以：要登录 / 要联网排行的大游戏，本地是玩不了的（这是设计如此，不是 bug）。
// =============================================================================
window.h5api = {
	initGame: function () {
		console.warn("试图调用 api: initGame");
	},
	/**
	 * 设置进度条进度
	 * @param {int} num 范围1~100，进度值
	 */
	progress: function (num) {
		console.warn("试图调用 api: progress");
	},
	/**
	 * 提交分数
	 * @param {int} score 分数
	 * @param {func} callback 回调函数
	 */
	submitScore: function (score, callback) {
		console.warn("试图调用 api: submitScore");
	},
	/**
	 * 获得排行榜
	 * @param {func} callback 回调函数
	 */
	getRank: function (callback) {
		console.warn("试图调用 api: getRank");
	},
	/**
	 * 是否能播放广告
	 * @param {func} callback 回调函数
	 * @returns {boolean} 是否能播放广告
	 */
	canPlayAd: function (callback) {
		console.log("调用 api: canPlayAd");
		if (callback) {
			callback({ canPlayAd: true });
		}
		return true;
	},
	/**
	 * 播放广告（直接告诉游戏“播完了”，等于 0 秒广告）
	 * @param {func} callback 回调函数
	 */
	playAd: function (callback) {
		console.log("调用 api: playAd");
		callback({ code: 10000, message: "开始播放" });
		callback({ code: 10001, message: "播放结束" });
	},
	/**
	 * 播放优量汇广告(仅供测试)
	 * @param {String} placementId 广告位Id
	 * @param {func} callback 回调函数
	 */
	playYlhAd: function (placementId, callback) {
		console.warn("试图调用 api: playYlhAd");
	},
	/**
	 * 播放插屏广告（它没有回调函数）
	 */
	playInterstitialAd: function () {
		console.warn("试图调用 api: playInterstitialAd");
	},
	/**
	 * 调用分享功能
	 */
	share: function () {
		console.warn("试图调用 api: share");
	},
	/**
	 * 获得用户当前是否登录
	 */
	isLogin: function () {
		console.log("调用 api: isLogin");
		return false;
	},
	/**
	 * 用户登录
	 * @param {func} callback 回调函数
	 */
	login: function (callback) {
		console.warn("试图调用 api: login");
	},
	/**
	 * 获得用户头像地址，高宽为120*120像素
	 */
	getUserAvatar: function (uid, size) {
		console.warn("试图调用 api: getUserAvatar");
	},
	/**
	 * 获得用户小头像地址，高宽为48*48像素
	 */
	getUserSmallAvatar: function (uid) {
		console.warn("试图调用 api: getUserSmallAvatar");
	},
	/**
	 * 获得用户大头像地址，高宽为200*200像素
	 */
	getUserBigAvatar: function (uid) {
		console.warn("试图调用 api: getUserBigAvatar");
	},
	/**
	 * 获得用户超大头像地址，高宽为360*360像素
	 */
	getUserLargeAvatar: function (uid) {
		console.warn("试图调用 api: getUserLargeAvatar");
	},
	/**
	 * 提交排名
	 */
	submitRanking: function (score, callback) {
		console.warn("试图调用 api: submitRanking");
	},
	/**
	 * 新版提交排名
	 */
	submitRankScore: function (rankId, score, callback) {
		console.warn("试图调用 api: submitRankScore");
	},
	/**
	 * 获得我的排名
	 */
	getMyRanking: function (callback) {
		console.warn("试图调用 api: getMyRanking");
	},
	/**
	 * 获得排名列表
	 */
	getRanking: function (callback, page, step) {
		console.warn("试图调用 api: getRanking");
	},
	/**
	 * 展示排行榜列表面板
	 */
	showRanking: function () {
		console.warn("试图调用 api: showRanking");
	},
	/**
	 * 展示新版排行榜面板
	 */
	showRankList: function () {
		console.warn("试图调用 api: showRankList");
	},
	/**
	 * 获得我附近排名列表
	 */
	getNearRanking: function (callback, step) {
		console.warn("试图调用 api: getNearRanking");
	},
	/**
	 * 敏感词检查
	 */
	checkWord: function (word, callback) {
		console.warn("试图调用 api: checkWord");
	},
	/**
	 * 展示推荐面板
	 */
	showRecommend: function () {
		console.warn("试图调用 api: showRecommend");
	},
	/**
	 * 存档
	 * @param {*} params.more 是否是多档 true | false
	 * @param {*} params.type 操作类型 write | read
	 * @param {*} params.title 存档标题 type为write时必填
	 * @param {*} params.data 存档数据 type为write时必填
	 * @param {*} params.callback 回调函数
	 */
	save: function (params) {
		console.warn("试图调用 api: save");
	},
	/**
	 * 游戏模式
	 * @param {*} mode 1 游客 2 账户，不传则打开面板
	 */
	gameMode: function (mode) {
		console.warn("试图调用 api: gameMode");
	},
	/**
	 * 显示引导面板
	 */
	showGuide: function (callback, index) {
		console.warn("试图调用 api: showGuide");
	},
	/**
	 * 检查API是否能使用
	 */
	checkAPI: function () {
		console.warn('正在使用本地替代 api，许多功能（登录/排行/云存档）无法使用');
	}
};
window.h5api.checkAPI();
