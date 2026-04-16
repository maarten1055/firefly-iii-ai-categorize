import express from "express";
import {getConfigVariable} from "./util.js";
import FireflyService from "./FireflyService.js";
import OpenAiService from "./OpenAiService.js";
import MistralService from "./MistralService.js";
import {Server} from "socket.io";
import * as http from "http";
import Queue from "queue";
import JobList from "./JobList.js";
import {MissingEnvironmentVariableException} from "./util.js";

export default class App {
    static #VALID_TRANSACTION_TRIGGERS = ["STORE_TRANSACTION", "TRIGGER_STORE_TRANSACTION", "UPDATE_TRANSACTION", "TRIGGER_UPDATE_TRANSACTION"];
    static #VALID_TRANSACTION_RESPONSES = ["TRANSACTIONS", "RESPONSE_TRANSACTIONS"];
    static #DESTINATION_HISTORY_LIMIT = 5;

    #PORT;
    #ENABLE_UI;

    #firefly;
    //#openAi;
    #mistral;

    #server;
    #io;
    #express;

    #queue;
    #jobList;


    constructor() {
        this.#PORT = getConfigVariable("PORT", '3000');
        this.#ENABLE_UI = getConfigVariable("ENABLE_UI", 'false') === 'true';
    }

    async run() {
        this.#queue = new Queue({
            timeout: 30 * 1000,
            concurrency: 1,
            autostart: true
        });

        this.#queue.addEventListener('start', event => console.log('Job started', event.detail.job))
        this.#queue.addEventListener('success', event => console.log('Job success', event.detail.job))
        this.#queue.addEventListener('error', event => console.error('Job error', event.detail.job, event.detail.error))
        this.#queue.addEventListener('timeout', event => console.log('Job timeout', event.detail.job))

        this.#express = express();
        this.#server = http.createServer(this.#express)
        this.#io = new Server(this.#server)

        this.#jobList = new JobList();
        this.#jobList.on('job created', data => this.#io.emit('job created', data));
        this.#jobList.on('job updated', data => this.#io.emit('job updated', data));

        this.#express.use(express.json());

        if (this.#ENABLE_UI) {
            this.#express.use('/', express.static('public'))
        } else {
            this.#express.get('/', (req, res) => {
                res.status(200).type('text/plain').send('Web UI is disabled. Set ENABLE_UI=true and restart the application to serve the UI at /.');
            });
        }

        this.#express.get('/api/diagnostics', this.#onDiagnostics.bind(this))
        this.#express.get('/api/transactions/options', this.#onGetTransactionOptions.bind(this))
        this.#express.get('/api/transactions/uncategorized', this.#onGetUncategorizedTransactions.bind(this))
        this.#express.post('/api/transactions/classify', this.#onClassifyTransaction.bind(this))
        this.#express.post('/api/transactions/apply', this.#onApplyTransactionUpdate.bind(this))
        this.#express.post('/api/jobs/:id/fill-missing', this.#onFillMissingJobValue.bind(this))
        this.#express.post('/webhook', this.#onWebhook.bind(this))

        this.#server.listen(this.#PORT, async () => {
            console.log(`Application running on port ${this.#PORT}`);
        });

        this.#io.on('connection', socket => {
            console.log('connected');
            socket.emit('jobs', Array.from(this.#jobList.getJobs().values()));
        })
    }

    #getFireflyService() {
        if (!this.#firefly) {
            this.#firefly = new FireflyService();
        }

        return this.#firefly;
    }

    #getMistralService() {
        if (!this.#mistral) {
            this.#mistral = new MistralService();
        }

        return this.#mistral;
    }

    #getCompletionStatus(data) {
        const hasCategory = Boolean(data?.category);
        const hasBudget = Boolean(data?.budget);

        return hasCategory !== hasBudget ? "partial" : "finished";
    }

    #getClassificationStatus(data) {
        const hasCategory = Boolean(data?.category);
        const hasBudget = Boolean(data?.budget);

        if (hasCategory && hasBudget) {
            return "ready";
        }

        if (hasCategory || hasBudget) {
            return "partial";
        }

        return "unclassified";
    }

    async #classifyTransactionSelection(transaction) {
        const destinationName = transaction.destinationName ?? transaction.destination_name;
        const description = transaction.description;
        const existingCategory = transaction.category ?? transaction.category_name ?? null;
        const existingBudget = transaction.budget ?? transaction.budget_name ?? null;
        const transactionJournalId = transaction.transactionJournalId ?? transaction.transaction_journal_id ?? null;

        if (!description) {
            throw new WebhookException("Missing transaction.description");
        }

        if (!destinationName) {
            throw new WebhookException("Missing transaction.destinationName");
        }

        const firefly = this.#getFireflyService();
        const mistral = this.#getMistralService();
        const categories = await firefly.getCategories();
        const budgets = await firefly.getBudgets();
        let recentTransactions = [];

        try {
            recentTransactions = await firefly.getRecentTransactionsForDestination(
                destinationName,
                App.#DESTINATION_HISTORY_LIMIT,
                transactionJournalId
            );
        } catch (error) {
            console.warn(`Could not load recent transactions for destination "${destinationName}": ${error.message}`);
        }

        const allLists = new Map();
        allLists.set('categories', Array.from(categories.keys()));
        allLists.set('budgets', Array.from(budgets.keys()));

        const classification = await mistral.classify(allLists, destinationName, description, recentTransactions);
        const data = {
            category: existingCategory ?? classification?.category ?? null,
            budget: existingBudget ?? classification?.budget ?? null,
            prompt: classification?.prompt ?? null,
            response: classification?.response ?? null,
            historyCount: recentTransactions.length,
            error: null,
        };

        return {
            categories,
            budgets,
            data,
        };
    }

    async #onGetUncategorizedTransactions(req, res) {
        try {
            const page = Math.max(1, Number.parseInt(req.query?.page, 10) || 1);
            const limit = Math.max(1, Math.min(Number.parseInt(req.query?.limit, 10) || 20, 100));
            const result = await this.#getFireflyService().getUncategorizedTransactions(page, limit);

            res.status(200).json({
                ok: true,
                ...result,
            });
        } catch (error) {
            console.error('Could not load uncategorized transactions:', error);
            res.status(500).json({
                ok: false,
                error: error.message,
            });
        }
    }

    async #onGetTransactionOptions(req, res) {
        try {
            const firefly = this.#getFireflyService();
            const categories = Array.from((await firefly.getCategories()).keys()).sort((left, right) => left.localeCompare(right));
            const budgets = Array.from((await firefly.getBudgets()).keys()).sort((left, right) => left.localeCompare(right));

            res.status(200).json({
                ok: true,
                categories,
                budgets,
            });
        } catch (error) {
            console.error('Could not load transaction options:', error);
            res.status(500).json({
                ok: false,
                error: error.message,
            });
        }
    }

    async #onClassifyTransaction(req, res) {
        try {
            const transaction = req.body?.transaction;

            if (!transaction) {
                throw new WebhookException("Missing transaction payload.");
            }

            const {data} = await this.#classifyTransactionSelection(transaction);

            res.status(200).json({
                ok: true,
                classification: {
                    ...data,
                    status: this.#getClassificationStatus(data),
                    canUpdate: Boolean(data.category || data.budget),
                }
            });
        } catch (error) {
            console.error('Could not classify transaction:', error);
            res.status(error instanceof WebhookException ? 400 : 500).json({
                ok: false,
                error: error.message,
            });
        }
    }

    async #onApplyTransactionUpdate(req, res) {
        try {
            const transaction = req.body?.transaction;
            const classification = req.body?.classification;
            const selections = req.body?.selections ?? {};

            if (!transaction) {
                throw new WebhookException("Missing transaction payload.");
            }

            if (!transaction.transactionId || !Array.isArray(transaction.transactions) || transaction.transactions.length === 0) {
                throw new WebhookException("This transaction does not contain enough Firefly III data to update.");
            }

            const firefly = this.#getFireflyService();
            const categories = await firefly.getCategories();
            const budgets = await firefly.getBudgets();
            const currentCategory = transaction.category ?? null;
            const currentBudget = transaction.budget ?? null;
            const selectedCategory = currentCategory ?? selections.category ?? classification?.category ?? null;
            const selectedBudget = currentBudget ?? selections.budget ?? classification?.budget ?? null;
            const categoryId = currentCategory === null && selectedCategory && categories.has(selectedCategory)
                ? categories.get(selectedCategory)
                : -1;
            const budgetId = currentBudget === null && selectedBudget && budgets.has(selectedBudget)
                ? budgets.get(selectedBudget)
                : -1;

            if (categoryId === -1 && budgetId === -1) {
                throw new WebhookException("No valid category or budget is available to update.");
            }

            await firefly.setCategoryAndBudget(transaction.transactionId, transaction.transactions, categoryId, budgetId);

            const updatedTransaction = {
                ...transaction,
                category: currentCategory ?? selectedCategory ?? null,
                budget: currentBudget ?? selectedBudget ?? null,
            };

            res.status(200).json({
                ok: true,
                transaction: updatedTransaction,
                status: this.#getClassificationStatus(updatedTransaction),
            });
        } catch (error) {
            console.error('Could not apply transaction update:', error);
            res.status(error instanceof WebhookException ? 400 : 500).json({
                ok: false,
                error: error.message,
            });
        }
    }

    async #onFillMissingJobValue(req, res) {
        const job = this.#jobList.getJob(req.params.id);

        if (!job) {
            res.status(404).json({
                ok: false,
                error: `Job ${req.params.id} was not found.`
            });
            return;
        }

        try {
            const categoryName = job.data?.category ?? null;
            const budgetName = job.data?.budget ?? null;

            if ((!categoryName && !budgetName) || (categoryName && budgetName)) {
                throw new WebhookException("This job is not partially processed.");
            }

            if (!job.data?.transactionId || !Array.isArray(job.data?.transactions) || job.data.transactions.length === 0) {
                throw new WebhookException("This job does not contain enough transaction data to update Firefly III.");
            }

            const firefly = this.#getFireflyService();
            const categories = await firefly.getCategories();
            const budgets = await firefly.getBudgets();
            let categoryId = -1;
            let budgetId = -1;
            let copiedValue = null;
            let updatedField = null;

            if (categoryName && !budgetName) {
                if (!budgets.has(categoryName)) {
                    throw new WebhookException(`No budget exists with the same name as category "${categoryName}".`);
                }

                budgetId = budgets.get(categoryName);
                copiedValue = categoryName;
                updatedField = "budget";
            } else if (budgetName && !categoryName) {
                if (!categories.has(budgetName)) {
                    throw new WebhookException(`No category exists with the same name as budget "${budgetName}".`);
                }

                categoryId = categories.get(budgetName);
                copiedValue = budgetName;
                updatedField = "category";
            }

            await firefly.setCategoryAndBudget(job.data.transactionId, job.data.transactions, categoryId, budgetId);

            const updatedData = {
                ...(job.data ?? {}),
                category: categoryName ?? copiedValue,
                budget: budgetName ?? copiedValue,
                error: null,
                manualUpdate: `Filled missing ${updatedField} with "${copiedValue}".`,
            };

            this.#jobList.updateJobData(job.id, updatedData);
            this.#jobList.setJobFinished(job.id, this.#getCompletionStatus(updatedData));

            res.status(200).json({
                ok: true,
                job: this.#jobList.getJob(job.id)
            });
        } catch (error) {
            console.error(`Manual fill for job ${job.id} failed:`, error);
            this.#jobList.updateJobData(job.id, {
                ...(job.data ?? {}),
                error: error.message,
            });

            res.status(error instanceof WebhookException ? 400 : 500).json({
                ok: false,
                error: error.message,
            });
        }
    }

    async #onDiagnostics(req, res) {
        const diagnostics = await Promise.all([
            this.#runCheck('firefly', async () => await this.#getFireflyService().diagnose()),
            this.#runCheck('mistral', async () => await this.#getMistralService().diagnose())
        ]);

        const ok = diagnostics.every(check => check.ok);
        res.status(ok ? 200 : 503).json({
            ok,
            checks: diagnostics
        });
    }

    async #runCheck(name, fn) {
        try {
            const details = await fn();
            return {
                name,
                ok: true,
                details
            };
        } catch (error) {
            return {
                name,
                ok: false,
                error: error instanceof MissingEnvironmentVariableException
                    ? `Missing environment variable: ${error.variableName}`
                    : error.message
            };
        }
    }

    #onWebhook(req, res) {
        try {
            console.info("Webhook triggered");
            this.#handleWebhook(req, res);
            res.send("Queued");
        } catch (e) {
            console.error(e)
            res.status(400).send(e.message);
        }
    }

    #handleWebhook(req, res) {
        // TODO: validate auth

        if (!App.#VALID_TRANSACTION_TRIGGERS.includes(req.body?.trigger)) {
            throw new WebhookException(`Unsupported webhook trigger "${req.body?.trigger}". Expected one of: ${App.#VALID_TRANSACTION_TRIGGERS.join(", ")}.`);
        }

        if (!App.#VALID_TRANSACTION_RESPONSES.includes(req.body?.response)) {
            throw new WebhookException(`Unsupported webhook response "${req.body?.response}". Expected one of: ${App.#VALID_TRANSACTION_RESPONSES.join(", ")}.`);
        }

        if (!req.body?.content?.id) {
            throw new WebhookException("Missing content.id");
        }

        if (req.body?.content?.transactions?.length === 0) {
            throw new WebhookException("No transactions are available in content.transactions");
        }

        const transaction = req.body.content.transactions[0];

        if (transaction.type !== "withdrawal") {
            throw new WebhookException("content.transactions[0].type has to be 'withdrawal'. Transaction will be ignored.");
        }

        if (transaction.category_name !== null && transaction.budget_name !== null) {
            throw new WebhookException("content.transactions[0].category_name and content.transactions[0].budget_name are already set. Transaction will be ignored.");
        }

        if (!transaction.description) {
            throw new WebhookException("Missing content.transactions[0].description");
        }

        if (!transaction.destination_name) {
            throw new WebhookException("Missing content.transactions[0].destination_name");
        }

        const destinationName = transaction.destination_name;
        const description = transaction.description

        const job = this.#jobList.createJob({
            destinationName,
            description,
            transactionId: req.body.content.id,
            transactions: req.body.content.transactions,
            category: transaction.category_name ?? null,
            budget: transaction.budget_name ?? null,
            error: null,
            manualUpdate: null,
        });

        this.#queue.push(async () => {
            this.#jobList.setJobInProgress(job.id);

            try {
                const {categories, budgets, data} = await this.#classifyTransactionSelection({
                    ...transaction,
                    transactionJournalId: transaction.transaction_journal_id ?? null,
                });

                const newData = Object.assign({}, job.data, data, {
                    manualUpdate: null,
                });

                this.#jobList.updateJobData(job.id, newData);

                const category_id = transaction.category_name === null && categories.has(data.category)
                    ? categories.get(data.category)
                    : -1;
                const budget_id = transaction.budget_name === null && budgets.has(data.budget)
                    ? budgets.get(data.budget)
                    : -1;

                if (category_id !== -1 || budget_id !== -1) {
                    await this.#getFireflyService().setCategoryAndBudget(req.body.content.id, req.body.content.transactions, category_id, budget_id);
                }

                this.#jobList.setJobFinished(job.id, this.#getCompletionStatus(newData));
            } catch (error) {
                console.error(`Job ${job.id} failed:`, error);
                this.#jobList.setJobFailed(job.id, error.message);
            }
        });
    }
}

class WebhookException extends Error {

    constructor(message) {
        super(message);
    }
}
