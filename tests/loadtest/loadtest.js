// LTAI v7.0 Load Test — k6 Script
//
// Usage:
//   k6 run loadtest.js --vus 100 --duration 60s
//
// Environment variables:
//   LTAI_BASE_URL  — API base URL (default: http://localhost:8080)
//   LTAI_API_KEY   — API key (optional, for authenticated endpoints)

import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { Trend, Rate, Counter } from 'k6/metrics';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.1.0/index.js';

// ═══════════════════════════════════════════════════════
// CONFIGURATION
// ═══════════════════════════════════════════════════════

const BASE_URL    = __ENV.LTAI_BASE_URL  || 'http://localhost:8080';
const API_KEY     = __ENV.LTAI_API_KEY   || '';
const BUDGET_MAX  = parseInt(__ENV.LTAI_BUDGET_MAX || '100000'); // token budget ceiling

// ═══════════════════════════════════════════════════════
// CUSTOM METRICS
// ═══════════════════════════════════════════════════════

const chatLatency   = new Trend('chat_latency', true);
const tokenCount    = new Trend('token_count', true);
const tokensTotal   = new Counter('tokens_total');
const errors5xx     = new Rate('errors_5xx');
const budgetExceeded = new Rate('budget_exceeded');

// ═══════════════════════════════════════════════════════
// TEST DATA — 5000 token (Chinese characters) input
// ═══════════════════════════════════════════════════════

// A ~5000 Chinese character environmental assessment query
const LONG_QUERY = `
请对以下化工项目进行全面的环境影响评价分析。

项目概况：
某化工企业计划在工业园区内新建一座年产50万吨的聚乙烯生产装置。项目位于北纬31.2度、东经121.5度，占地面积约200亩。项目周边5公里范围内有居民区、学校和农田。项目采用气相法聚乙烯生产工艺，主要原料为乙烯和氢气。

排放参数详细列表：
排放源参数：
- 主排气筒高度He=80米，直径D=2.5米
- 烟气排放温度Ts=420K
- 环境温度Ta=298K
- 排放速率Q=150g/s
- 风速u=3.5m/s，主导风向为东南风
- 大气稳定度等级为D级
- 二氧化硫排放浓度设计值：200mg/m³
- 氮氧化物排放浓度设计值：150mg/m³
- 颗粒物排放浓度设计值：30mg/m³

无组织排放：
- 储罐区VOCs无组织排放量：约50吨/年
- 装卸区逸散排放量：约10吨/年

废水排放：
- 生产废水量：5000m³/d
- COD浓度：300mg/L
- 氨氮浓度：25mg/L
- pH值：6.5-8.5

噪声源：
- 压缩机：95dB(A)
- 风机：90dB(A)
- 泵组：85dB(A)
- 距厂界最近距离：150米

固废产生：
- 废催化剂：200吨/年
- 废活性炭：50吨/年
- 污水处理污泥：3000吨/年
- 生活垃圾：100吨/年

请对以下方面进行详细评估：

一、大气环境影响评价
1. 采用高斯烟羽模型计算SO2、NOx和颗粒物的最大落地浓度及出现距离
2. 评价对周边环境空气敏感目标的影响
3. 核算大气环境防护距离
4. 预测非正常工况下的环境影响
5. 评估VOCs无组织排放对区域臭氧生成的影响

二、水环境影响评价
1. 分析废水处理工艺的可行性
2. 预测废水排放对受纳水体的影响
3. 评估地下水污染风险
4. 提出水环境保护措施

三、声环境影响评价
1. 预测厂界噪声值
2. 评价对周边声环境敏感目标的影响
3. 提出噪声控制措施

四、固体废物环境影响评价
1. 分类评价各类固废的环境影响
2. 提出固废处置方案
3. 评估危废临时贮存的风险

五、生态环境影响评价
1. 分析项目建设对区域生态系统的影响
2. 评价对生物多样性的影响
3. 提出生态保护和恢复措施

六、环境风险评价
1. 识别重大危险源
2. 预测最大可信事故的环境后果
3. 提出风险防范措施和应急预案

七、清洁生产与循环经济
1. 分析生产工艺的清洁生产水平
2. 提出节能减排措施
3. 评估资源循环利用潜力

八、环境管理与监测计划
1. 提出环境管理机构设置方案
2. 制定施工期和运营期环境监测计划
3. 估算环保投资

请引用适用的中国环境标准（包括但不限于GB 3095-2012、HJ 2.2-2018等），
提供定量的计算结果和置信区间估计。
每个评估方面至少包含500字的详细分析和数据支撑。
请特别注意排放参数是否在合理范围内，
并指出任何可能超标的风险因素。
`.trim();

// ═══════════════════════════════════════════════════════
// OPTIONS — thresholds and scenarios
// ═══════════════════════════════════════════════════════

export const options = {
    stages: [
        { duration: '30s',  target: 20  },   // ramp up
        { duration: '30s',  target: 50  },   // mid load
        { duration: '30s',  target: 100 },   // peak load
        { duration: '60s',  target: 100 },   // sustained peak
        { duration: '30s',  target: 0   },   // ramp down
    ],
    thresholds: {
        // P99 latency < 5 seconds
        'chat_latency':    ['p(99)<5000'],
        // No 5xx errors
        'errors_5xx':      ['rate<0.01'],
        // 95% of requests complete successfully
        'http_req_failed': ['rate<0.05'],
        // Token budget tracking — no single request blowing past budget
        'budget_exceeded': ['rate<0.01'],
    },
};

// ═══════════════════════════════════════════════════════
// SETUP — health check before test
// ═══════════════════════════════════════════════════════

export function setup() {
    console.log(`=== LTAI v7.0 Load Test Setup ===`);
    console.log(`Target: ${BASE_URL}`);
    console.log(`Max VUs: 100 | Stages: 30s/20 → 30s/50 → 30s/100 → 60s/100 → 30s/0`);

    // Health check
    const healthUrl = `${BASE_URL}/api/v7/health`;
    const healthResp = http.get(healthUrl, { timeout: '10s' });

    check(healthResp, {
        'health check returns 200': (r) => r.status === 200,
        'health check body has version': (r) => {
            try { return JSON.parse(r.body).version !== undefined; } catch { return false; }
        },
    });

    if (healthResp.status !== 200)
        throw new Error(`Health check failed: ${healthResp.status}. Is LTAI running at ${BASE_URL}?`);

    console.log(`Health: OK (version ${JSON.parse(healthResp.body).version})`);

    // Check /api/v7/status for initial budget state
    const statusUrl = `${BASE_URL}/api/v7/status`;
    const statusResp = http.get(statusUrl, { timeout: '10s' });
    if (statusResp.status === 200) {
        const status = JSON.parse(statusResp.body);
        console.log(`Status: safety_sessions=${status.safety_gate?.active_sessions || 0}`);
    }

    return {
        query: LONG_QUERY,
        budgetMax: BUDGET_MAX,
    };
}

// ═══════════════════════════════════════════════════════
// DEFAULT — main test function
// ═══════════════════════════════════════════════════════

export default function (data) {
    const headers = {
        'Content-Type': 'application/json',
    };
    if (API_KEY) {
        headers['Authorization'] = `Bearer ${API_KEY}`;
    }

    // === Primary endpoint: /api/chat (synchronous) ===

    const chatPayload = JSON.stringify({
        query: data.query,
        session_id: `loadtest-${__VU}-${__ITER}`,
    });

    const timestamp = Date.now();
    const chatResp = http.post(`${BASE_URL}/api/chat`, chatPayload, {
        headers,
        timeout: '30s',
        tags: { name: 'chat_sync' },
    });
    const latency = Date.now() - timestamp;

    chatLatency.add(latency);
    errors5xx.add(chatResp.status >= 500 ? 1 : 0);

    check(chatResp, {
        'chat: status is 200':          (r) => r.status === 200,
        'chat: response has text':      (r) => {
            try { return JSON.parse(r.body).text !== undefined; } catch { return false; }
        },
        'chat: P99 < 5s':               () => latency < 5000,
        'chat: no 5xx error':           (r) => r.status < 500,
    });

    // Track token budget from response
    try {
        const body = JSON.parse(chatResp.body);
        if (body.tokens) {
            tokenCount.add(body.tokens);
            tokensTotal.add(body.tokens);
            if (body.tokens > data.budgetMax / 100) { // per-request portion of budget
                budgetExceeded.add(1);
            }
        }
    } catch {}

    // === Secondary: /api/v7/status (observability) ===
    // Run every 10th iteration to check system health under load

    if (__ITER % 10 === 0) {
        const statusResp = http.get(`${BASE_URL}/api/v7/status`, {
            headers,
            timeout: '5s',
            tags: { name: 'v7_status' },
        });
        check(statusResp, {
            'status: returns 200': (r) => r.status === 200,
        });
    }

    // Simulate realistic user think time (1-3 seconds)
    sleep(Math.random() * 2 + 1);
}

// ═══════════════════════════════════════════════════════
// TEARDOWN — aggregate report
// ═══════════════════════════════════════════════════════

export function teardown(data) {
    console.log(`\n=== LTAI v7.0 Load Test Complete ===`);
    console.log(`Total tokens processed: ${tokensTotal.value || 'N/A'}`);
    console.log(`Budget ceiling: ${data.budgetMax}`);
}

// ═══════════════════════════════════════════════════════
// SUMMARY — custom JSON summary
// ═══════════════════════════════════════════════════════

export function handleSummary(data) {
    const summary = {
        timestamp: new Date().toISOString(),
        target: BASE_URL,
        test_type: 'chat_load',
        metrics: {
            chat_latency_ms: {
                avg:   data.metrics.chat_latency?.values?.avg   || 0,
                p50:   data.metrics.chat_latency?.values?.p(50) || 0,
                p90:   data.metrics.chat_latency?.values?.p(90) || 0,
                p99:   data.metrics.chat_latency?.values?.p(99) || 0,
                max:   data.metrics.chat_latency?.values?.max   || 0,
            },
            http_reqs: {
                total:        data.metrics.http_reqs?.values?.count || 0,
                failed:       data.metrics.http_req_failed?.values?.passes ? 'OK' : 'FAIL',
                failure_rate: data.metrics.http_req_failed?.values?.rate  || 0,
            },
            errors_5xx_rate: data.metrics.errors_5xx?.values?.rate || 0,
            tokens_total:  data.metrics.tokens_total?.values?.count  || 0,
            budget_violations: data.metrics.budget_exceeded?.values?.rate || 0,
        },
        thresholds: {
            'P99 < 5s':       data.metrics.chat_latency?.values?.p(99) < 5000    ? 'PASS' : 'FAIL',
            'errors_5xx < 1%': data.metrics.errors_5xx?.values?.rate    < 0.01   ? 'PASS' : 'FAIL',
            'failure < 5%':   data.metrics.http_req_failed?.values?.rate < 0.05   ? 'PASS' : 'FAIL',
            'budget < 1%':    data.metrics.budget_exceeded?.values?.rate < 0.01   ? 'PASS' : 'FAIL',
        },
    };

    return {
        'stdout': textSummary(data, { indent: '  ', enableColors: true }),
        'loadtest-summary.json': JSON.stringify(summary, null, 2),
    };
}
