import {getConfigVariable} from "./util.js";

const UNCATEGORIZED_CACHE_TTL_MS = 5 * 60 * 1000;

export default class FireflyService {
    #BASE_URL;
    #PERSONAL_TOKEN;
    #uncategorizedCache = null;
    #uncategorizedScanPromise = null;

    constructor() {
        this.#BASE_URL = getConfigVariable("FIREFLY_URL")
        if (this.#BASE_URL.slice(-1) === "/") {
            this.#BASE_URL = this.#BASE_URL.substring(0, this.#BASE_URL.length - 1)
        }

        this.#PERSONAL_TOKEN = getConfigVariable("FIREFLY_PERSONAL_TOKEN")
    }

    async getCategories() {
        const categories = new Map();

        for await (const category of this.#fetchCollection("/api/v1/categories")) {
            categories.set(category.attributes.name, category.id);
        }

        return categories;
    }

    async getBudgets() {
        const budgets = new Map();

        for await (const budget of this.#fetchCollection("/api/v1/budgets")) {
            budgets.set(budget.attributes.name, budget.id);
        }

        return budgets;
    }

    async getBills()    {
        const response = await this.#authorizedFetch(`${this.#BASE_URL}/api/v1/bills`);

        const data = await response.json();

        const bills = new Map();
        data.data.forEach(bill => {
            bills.set(bill.attributes.name, bill.id);
        });

        return bills;
    }

    async getUncategorizedTransactions(page = 1, limit = 20, destinationName = null) {
        const normalizedPage = Math.max(1, Number.parseInt(page, 10) || 1);
        const normalizedLimit = Math.max(1, Math.min(Number.parseInt(limit, 10) || 20, 100));
        const normalizedDestination = destinationName ? String(destinationName).trim() : null;

        this.#ensureUncategorizedCacheFresh();
        const startIndex = (normalizedPage - 1) * normalizedLimit;
        let filteredItems;

        if (normalizedDestination) {
            await this.#ensureUncategorizedCacheComplete();
            if (!this.#uncategorizedCache) {
                this.#ensureUncategorizedCacheFresh();
                await this.#ensureUncategorizedCacheComplete();
            }

            filteredItems = (this.#uncategorizedCache?.items ?? []).filter(item => item.destinationName === normalizedDestination);
        } else {
            const requiredCount = startIndex + normalizedLimit + 1;
            await this.#ensureUncategorizedCacheCount(requiredCount);
            if (!this.#uncategorizedCache) {
                this.#ensureUncategorizedCacheFresh();
                await this.#ensureUncategorizedCacheCount(requiredCount);
            }

            filteredItems = this.#uncategorizedCache?.items ?? [];
        }

        const items = filteredItems.slice(startIndex, startIndex + normalizedLimit + 1);

        return {
            page: normalizedPage,
            limit: normalizedLimit,
            hasNextPage: items.length > normalizedLimit,
            items: items.slice(0, normalizedLimit),
        };
    }

    async getUncategorizedMetadata(destinationName = null) {
        this.#ensureUncategorizedCacheFresh();
        await this.#ensureUncategorizedCacheComplete();

        if (!this.#uncategorizedCache) {
            this.#ensureUncategorizedCacheFresh();
            await this.#ensureUncategorizedCacheComplete();
        }

        const normalizedDestination = destinationName ? String(destinationName).trim() : null;

        const destinationSummaries = new Map();

        for (const item of this.#uncategorizedCache?.items ?? []) {
            const destinationName = item.destinationName || 'Unknown';

            if (!destinationSummaries.has(destinationName)) {
                destinationSummaries.set(destinationName, {
                    totalTransactions: 0,
                    withoutCategory: 0,
                    withoutBudget: 0,
                });
            }

            const destinationSummary = destinationSummaries.get(destinationName);
            destinationSummary.totalTransactions += 1;
            if (!item.category) {
                destinationSummary.withoutCategory += 1;
            }
            if (!item.budget) {
                destinationSummary.withoutBudget += 1;
            }
        }

        const filteredItems = normalizedDestination
            ? (this.#uncategorizedCache?.items ?? []).filter(item => item.destinationName === normalizedDestination)
            : (this.#uncategorizedCache?.items ?? []);

        return {
            summary: {
                totalTransactions: filteredItems.length,
                withoutCategory: filteredItems.filter(item => !item.category).length,
                withoutBudget: filteredItems.filter(item => !item.budget).length,
                destinationName: normalizedDestination,
            },
            destinations: Array.from(destinationSummaries.keys()).sort((left, right) => left.localeCompare(right)),
        };
    }

    async getUncategorizedTransactionsForDestination(destinationName) {
        const normalizedDestination = destinationName ? String(destinationName).trim() : null;

        if (!normalizedDestination) {
            return [];
        }

        this.#ensureUncategorizedCacheFresh();
        await this.#ensureUncategorizedCacheComplete();

        if (!this.#uncategorizedCache) {
            this.#ensureUncategorizedCacheFresh();
            await this.#ensureUncategorizedCacheComplete();
        }

        return (this.#uncategorizedCache?.items ?? []).filter(item => item.destinationName === normalizedDestination);
    }

    async getRecentTransactionsForDestination(destinationName, limit = 5, excludeJournalId = null) {
        const params = new URLSearchParams({
            query: `to:"${this.#escapeSearchValue(destinationName)}"`,
            page: "1",
            limit: String(Math.max(limit * 4, 20))
        });

        const response = await this.#authorizedFetch(`${this.#BASE_URL}/api/v1/search/transactions?${params.toString()}`);
        const data = await response.json();
        const transactionGroups = data.data ?? [];

        const matches = transactionGroups.flatMap(group =>
            (group.attributes?.transactions ?? []).map(transaction => ({
                id: transaction.transaction_journal_id,
                date: transaction.date,
                description: transaction.description,
                destinationName: transaction.destination_name,
                amount: transaction.amount,
                currencyCode: transaction.currency_code,
                category: transaction.category_name,
                budget: transaction.budget_name,
                type: transaction.type
            }))
        ).filter(transaction =>
            transaction.type === "withdrawal" &&
            transaction.destinationName === destinationName &&
            transaction.id !== excludeJournalId &&
            (transaction.category || transaction.budget)
        ).sort((a, b) => new Date(b.date) - new Date(a.date));

        return matches.slice(0, limit);
    }

    async setCategoryAndBudget(transactionId, transactions, categoryId, budgetId) {
        const tag = getConfigVariable("FIREFLY_TAG", "AI categorized");

        const body = {
            apply_rules: false,
            fire_webhooks: false,
            transactions: [],
        }

        transactions.forEach(transaction => {
            const tags = Array.from(new Set([...(transaction.tags ?? []), tag]));

            const object = {
                transaction_journal_id: transaction.transaction_journal_id,
                tags: tags,
            }

            if (categoryId !== -1) {
                object.category_id = categoryId;
            }

            if (budgetId !== -1) {
                object.budget_id = budgetId;
            }

            body.transactions.push(object);
        })

        const response = await this.#authorizedFetch(`${this.#BASE_URL}/api/v1/transactions/${transactionId}`, {
            method: "PUT",
            headers: {
                "Content-Type": "application/json",
            },
            body: JSON.stringify(body)
        });

        await response.json();
        this.#invalidateUncategorizedCache();
        console.info("Transaction updated")
    }

    #ensureUncategorizedCacheFresh() {
        if (this.#uncategorizedCache && Date.now() - this.#uncategorizedCache.createdAt < UNCATEGORIZED_CACHE_TTL_MS) {
            return;
        }

        this.#uncategorizedCache = {
            createdAt: Date.now(),
            items: [],
            nextApiPage: 1,
            finished: false,
        };
    }

    async #ensureUncategorizedCacheCount(requiredCount) {
        while (true) {
            const cache = this.#uncategorizedCache;

            if (!cache || cache.finished || cache.items.length >= requiredCount) {
                return;
            }

            if (this.#uncategorizedScanPromise) {
                await this.#uncategorizedScanPromise;
                continue;
            }

            this.#uncategorizedScanPromise = this.#scanNextUncategorizedPage();

            try {
                await this.#uncategorizedScanPromise;
            } finally {
                this.#uncategorizedScanPromise = null;
            }
        }
    }

    async #ensureUncategorizedCacheComplete() {
        await this.#ensureUncategorizedCacheCount(Number.MAX_SAFE_INTEGER);
    }

    async #scanNextUncategorizedPage() {
        if (!this.#uncategorizedCache || this.#uncategorizedCache.finished) {
            return;
        }

        const cache = this.#uncategorizedCache;

        const params = new URLSearchParams({
            page: String(cache.nextApiPage),
            limit: "100",
            type: "withdrawal"
        });

        const response = await this.#authorizedFetch(`${this.#BASE_URL}/api/v1/transactions?${params.toString()}`);
        const data = await response.json();
        const groups = data.data ?? [];

        if (this.#uncategorizedCache !== cache) {
            return;
        }

        for (const group of groups) {
            const transactions = group.attributes?.transactions ?? [];

            for (const transaction of transactions) {
                if (transaction.type !== "withdrawal") {
                    continue;
                }

                if (transaction.category_name !== null && transaction.budget_name !== null) {
                    continue;
                }

                cache.items.push({
                    transactionId: group.id,
                    transactionJournalId: transaction.transaction_journal_id,
                    date: transaction.date,
                    description: transaction.description,
                    destinationName: transaction.destination_name,
                    sourceName: transaction.source_name,
                    amount: transaction.amount,
                    currencyCode: transaction.currency_code,
                    category: transaction.category_name,
                    budget: transaction.budget_name,
                    tags: transaction.tags ?? [],
                    transactions,
                });
            }
        }

        if (groups.length === 0 || !data.meta?.pagination || cache.nextApiPage >= data.meta.pagination.total_pages) {
            cache.finished = true;
            return;
        }

        cache.nextApiPage += 1;
    }

    #invalidateUncategorizedCache() {
        this.#uncategorizedCache = null;
    }

    async diagnose() {
        const categories = await this.getCategories();
        const budgets = await this.getBudgets();

        return {
            ok: true,
            baseUrl: this.#BASE_URL,
            categories: categories.size,
            budgets: budgets.size
        };
    }

    async *#fetchCollection(path) {
        let page = 1;

        while (true) {
            const response = await this.#authorizedFetch(`${this.#BASE_URL}${path}?page=${page}`);

            const data = await response.json();
            const items = data.data ?? [];

            for (const item of items) {
                yield item;
            }

            if (items.length === 0 || !data.meta?.pagination || page >= data.meta.pagination.total_pages) {
                break;
            }

            page += 1;
        }
    }

    async #authorizedFetch(url, options = {}) {
        const response = await fetch(url, {
            ...options,
            headers: {
                Authorization: `Bearer ${this.#PERSONAL_TOKEN}`,
                ...(options.headers ?? {}),
            }
        });

        if (!response.ok) {
            throw new FireflyException(response.status, response, await response.text(), url)
        }

        return response;
    }

    #escapeSearchValue(value) {
        return String(value).replace(/(["\\])/g, "\\$1");
    }
}

class FireflyException extends Error {
    code;
    response;
    body;

    constructor(statusCode, response, body, url = null) {
        super(`Error while communicating with Firefly III${url ? ` at ${url}` : ""}: ${statusCode} - ${body}`);

        this.code = statusCode;
        this.response = response;
        this.body = body;
    }
}
