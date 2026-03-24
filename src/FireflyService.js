import {getConfigVariable} from "./util.js";

export default class FireflyService {
    #BASE_URL;
    #PERSONAL_TOKEN;

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
        console.info("Transaction updated")
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
